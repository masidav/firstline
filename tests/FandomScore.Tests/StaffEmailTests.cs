using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Notifications.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Staff notices by email (migration 0059): the setting, the queue trigger, and the job through a fake sender.</summary>
[Collection(PostgresCollection.Name)]
public sealed class StaffEmailTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeEmailSender _sender = new();
    private int _runs;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"email_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task The_setting_is_on_by_default_and_changes_with_the_profile()
    {
        var token = await SignInAsync("staffer");
        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        Assert.True(me.GetProperty("email_settings").GetProperty("staff").GetBoolean());

        var updated = await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", token, new { email_settings = new { staff = false } }), HttpStatusCode.OK);
        Assert.False(updated.GetProperty("email_settings").GetProperty("staff").GetBoolean());
        Assert.True(updated.GetProperty("push").GetProperty("staff").GetBoolean());   // the push setting is separate
        Assert.False(await ScalarAsync<bool>("SELECT email_staff FROM users WHERE handle = 'staffer'"));

        var bad = await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", token, new { email_settings = new { staff = "yes" } }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("email_settings.staff", bad.GetProperty("errors")[0].GetProperty("field").GetString());
        var notObject = await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", token, new { email_settings = true }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("email_settings", notObject.GetProperty("errors")[0].GetProperty("field").GetString());

        // The data download shows it too.
        var export = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/export", token), HttpStatusCode.OK);
        Assert.False(export.GetProperty("account").GetProperty("profile").GetProperty("email_settings").GetProperty("staff").GetBoolean());
    }

    [Fact]
    public async Task A_staff_notice_queues_an_email_for_staff_who_want_one_and_nothing_else_does()
    {
        await SignInAsync("staffer");
        await SignInAsync("quiet_staffer");
        await SignInAsync("fan");
        await ExecuteAsync("UPDATE users SET is_staff = true WHERE handle IN ('staffer', 'quiet_staffer')");
        await ExecuteAsync("UPDATE users SET email_staff = false WHERE handle = 'quiet_staffer'");

        var wanted = await NotifyAsync("staffer", "staff_report", new { target_type = "photo", reason = "spam" });
        var declined = await NotifyAsync("quiet_staffer", "staff_report", new { target_type = "photo", reason = "spam" });
        var fanNotice = await NotifyAsync("fan", "presale", new { event_id = Guid.NewGuid(), presale_name = "Fan club" });

        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'notifications.email' AND payload->>'notification_id' = @id", new { id = wanted.ToString() }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'notifications.email' AND payload->>'notification_id' = @id", new { id = declined.ToString() }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'notifications.email' AND payload->>'notification_id' = @id", new { id = fanNotice.ToString() }));
    }

    [Fact]
    public async Task The_job_emails_the_in_app_text_with_a_link_once_and_records_it()
    {
        await SignInAsync("staffer");
        await ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'staffer'");
        var notificationId = await NotifyAsync("staffer", "staff_review", new { handle = "ana", kind = "ticket_stub", artist_name = "Geese" });

        await EmailAsync(notificationId);
        await EmailAsync(notificationId);   // a retried job sends nothing twice

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal("staffer@example.test", sent.To);
        Assert.Equal("Fandom Score: Proof to review", sent.Subject);
        Assert.Contains("@ana sent ticket stub for Geese.", sent.Text);
        Assert.Contains("Open it: https://app.example.test/review", sent.Text);
        Assert.Contains("Staff notices by email", sent.Text);
        Assert.Equal(("sent", "fake-1"), await ScalarAsync<(string, string)>(
            "SELECT status, provider_message_id FROM email_deliveries WHERE notification_id = @notificationId", new { notificationId }));
    }

    [Fact]
    public async Task Nothing_is_sent_when_email_is_off_read_stale_declined_or_not_staff()
    {
        await SignInAsync("staffer");
        await SignInAsync("fan");
        await ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'staffer'");
        var data = new { target_type = "log", reason = "abuse" };

        // Off: the job is a no-op, and the notice can still be emailed once it is on.
        var fresh = await NotifyAsync("staffer", "staff_report", data);
        await EmailAsync(fresh, new EmailOptions { Enabled = false });
        Assert.Empty(_sender.Sent);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM email_deliveries"));

        // Read since.
        var read = await NotifyAsync("staffer", "staff_report", data);
        await ExecuteAsync("UPDATE notifications SET read_at = now() WHERE id = @read", new { read });
        await EmailAsync(read);

        // Too old to be useful (written 13 hours ago; only read_at may change after a row is written, so it is inserted old).
        var stale = await NotifyAsync("staffer", "staff_report", data, age: TimeSpan.FromHours(13));
        await EmailAsync(stale);

        // Turned off between the queue and the send.
        var declined = await NotifyAsync("staffer", "staff_report", data);
        await ExecuteAsync("UPDATE users SET email_staff = false WHERE handle = 'staffer'");
        await EmailAsync(declined);
        await ExecuteAsync("UPDATE users SET email_staff = true WHERE handle = 'staffer'");

        // A fan's notice never goes by email, whatever the job is handed.
        var fanNotice = await NotifyAsync("fan", "presale", new { event_id = Guid.NewGuid(), presale_name = "Fan club" });
        await EmailAsync(fanNotice);

        // The one that was only waiting for email to be turned on.
        await EmailAsync(fresh);

        Assert.Equal(fresh, Assert.Single(_sender.Sent).NotificationId);
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM email_deliveries"));
    }

    [Fact]
    public async Task A_refusal_is_recorded_and_an_outage_is_retried()
    {
        await SignInAsync("staffer");
        await ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'staffer'");
        var refused = await NotifyAsync("staffer", "staff_appeal", new { handle = "ben" });
        _sender.Refuse = "mailbox unavailable";
        await EmailAsync(refused);
        Assert.Equal(("failed", "mailbox unavailable"), await ScalarAsync<(string, string)>(
            "SELECT status, error FROM email_deliveries WHERE notification_id = @refused", new { refused }));

        var during = await NotifyAsync("staffer", "staff_appeal", new { handle = "cat" });
        _sender.Refuse = null;
        _sender.Down = true;
        await Assert.ThrowsAsync<EmailUnavailableException>(() => EmailAsync(during));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM email_deliveries WHERE notification_id = @during", new { during }));

        _sender.Down = false;
        await EmailAsync(during);
        Assert.Equal("sent", await ScalarAsync<string>("SELECT status FROM email_deliveries WHERE notification_id = @during", new { during }));
    }

    [Fact]
    public void Settings_are_checked_before_the_worker_starts()
    {
        Assert.Null(new EmailOptions().Problem());   // off needs nothing
        Assert.Null(Options().Value.Problem());
        Assert.Contains("Email__From", new EmailOptions { Enabled = true, AppOrigin = "https://app.example.test" }.Problem());
        Assert.Contains("Email__AppOrigin", new EmailOptions { Enabled = true, From = "a@example.test", AppOrigin = "https://app.example.test/home" }.Problem());
        Assert.Contains("Email__AppOrigin", new EmailOptions { Enabled = true, From = "a@example.test", AppOrigin = "app.example.test" }.Problem());
        Assert.Contains("Email__Provider", new EmailOptions { Enabled = true, From = "a@example.test", AppOrigin = "https://app.example.test", Provider = "sendgrid" }.Problem());
        Assert.Null(new EmailOptions { Enabled = true, From = "Fandom Score <no-reply@example.test>", AppOrigin = "https://app.example.test/" }.Problem());
    }

    [Fact]
    public async Task The_log_sender_writes_the_email_to_the_log_and_answers_ok()
    {
        var sender = new LogEmailSender(Options().Value, NullLogger<LogEmailSender>.Instance);
        var result = await sender.SendAsync(new EmailMessage("staffer@example.test", "Subject", "Text"), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.NotNull(result.MessageId);
    }

    private async Task EmailAsync(Guid notificationId, EmailOptions? options = null)
    {
        var job = new StaffEmailJob(_db, _sender, Microsoft.Extensions.Options.Options.Create(options ?? Options().Value), TimeProvider.System,
            NullLogger<StaffEmailJob>.Instance);
        Assert.Equal("notifications.email", job.Kind);
        _sender.Current = notificationId;
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, $$"""{"notification_id":"{{notificationId}}"}""", 1, 10), CancellationToken.None);
    }

    private static IOptions<EmailOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new EmailOptions { Enabled = true, From = "Fandom Score <no-reply@example.test>", AppOrigin = "https://app.example.test" });

    private async Task<Guid> NotifyAsync(string handle, string kind, object data, TimeSpan? age = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO notifications (user_id, kind, subject_id, data, created_at)
            SELECT id, @kind, gen_random_uuid(), @data::jsonb, now() - @age FROM users WHERE handle = @handle RETURNING id
            """,
            new { handle, kind, data = JsonSerializer.Serialize(data), age = age ?? TimeSpan.Zero });
    }

    private async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.QuerySingleAsync<T>(sql, args))!;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Records every email; refuses them all with <see cref="Refuse"/>, or is down with <see cref="Down"/>.</summary>
    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(Guid NotificationId, string To, string Subject, string Text)> Sent { get; } = [];

        public Guid Current { get; set; }

        public string? Refuse { get; set; }

        public bool Down { get; set; }

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new EmailUnavailableException("the fake provider is down");
            }

            if (Refuse is { } reason)
            {
                return Task.FromResult(new EmailResult(false, Error: reason));
            }

            Sent.Add((Current, message.To, message.Subject, message.Text));
            return Task.FromResult(new EmailResult(true, $"fake-{Sent.Count}"));
        }
    }
}
