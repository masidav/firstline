using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Push notifications (migration 0032): device registration, settings, sending through a fake Expo, receipts.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PushNotificationTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string AnaPhone = "ExponentPushToken[ana-phone-0001]";
    private const string AnaTablet = "ExponentPushToken[ana-tablet-002]";
    private readonly FakeExpo _expo = new();
    private int _runs;
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"push-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"push_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    public void Dispose() => _expo.Dispose();

    [Fact]
    public async Task Devices_register_move_to_the_last_fan_signed_in_and_can_be_removed()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");

        var bad = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = "not-a-token", platform = "web" }),
            HttpStatusCode.UnprocessableEntity);
        Assert.Equal(2, bad.GetProperty("errors").GetArrayLength());

        var device = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        var again = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        Assert.Equal(device.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        // Ben signs in on Ana's phone: the token is his now.
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ben, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        Assert.Equal("ben", await ScalarAsync<string>("SELECT u.handle::text FROM push_devices d JOIN users u ON u.id = d.user_id WHERE d.token = @AnaPhone", new { AnaPhone }));

        // Ana can't remove Ben's device; Ben can.
        var id = device.GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/me/push-devices/{id}", ana), HttpStatusCode.NoContent);
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM push_devices"));
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/me/push-devices/{id}", ben), HttpStatusCode.NoContent);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM push_devices"));
    }

    [Fact]
    public async Task A_notification_is_pushed_to_each_live_device_once_with_the_in_app_text_and_the_unread_badge()
    {
        var ana = await SignInAsync("ana");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaTablet, platform = "android" }), HttpStatusCode.OK);
        await NotifyAsync("ana", "draw_not_selected", new { event_id = Guid.NewGuid(), event_name = "Geese at Metro" });
        var id = await NotifyAsync("ana", "ticket_delivered", new { event_id = Guid.NewGuid(), event_name = "Geese at Metro", starts_at = "2026-11-20T02:00:00Z", seats = 1 });
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'notifications.push' AND payload->>'notification_id' = @id",
            new { id = id.ToString() }));

        await PushAsync(id);
        await PushAsync(id);   // a retry reaches no device twice

        var sent = _expo.Sent.ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal([AnaPhone, AnaTablet], sent.Select(m => m.GetProperty("to").GetString()));
        var first = sent[0];
        Assert.Equal(("Your ticket is here", "/tickets", 2), (first.GetProperty("title").GetString(),
            first.GetProperty("data").GetProperty("link").GetString(), first.GetProperty("badge").GetInt32()));
        Assert.Equal("Your ticket for Geese at Metro on Nov 20 has been delivered. Open Tickets to show it at the door.", first.GetProperty("body").GetString());
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM push_deliveries WHERE notification_id = @id AND status = 'sent' AND ticket_id IS NOT NULL", new { id }));
    }

    [Fact]
    public async Task Settings_old_or_read_notifications_and_unregistered_devices_stop_pushes()
    {
        var ana = await SignInAsync("ana");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", ana), HttpStatusCode.OK);
        Assert.Equal((true, true, true), (me.GetProperty("push").GetProperty("tickets").GetBoolean(),
            me.GetProperty("push").GetProperty("presales").GetBoolean(), me.GetProperty("push").GetProperty("staff").GetBoolean()));
        Assert.False(me.TryGetProperty("push_tickets", out _));
        var updated = await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", ana, new { push = new { presales = false } }), HttpStatusCode.OK);
        Assert.Equal((true, false), (updated.GetProperty("push").GetProperty("tickets").GetBoolean(), updated.GetProperty("push").GetProperty("presales").GetBoolean()));
        await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", ana, new { push = new { tickets = "yes" } }), HttpStatusCode.UnprocessableEntity);

        var presale = await NotifyAsync("ana", "presale", new { event_id = Guid.NewGuid(), presale_name = "Fan Presale" });
        var read = await NotifyAsync("ana", "ticket_delivered", new { event_name = "Read already" });
        var old = await NotifyAsync("ana", "ticket_delivered", new { event_name = "Stale" });
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE notifications SET read_at = now() WHERE id = @read;
                ALTER TABLE notifications DISABLE TRIGGER trg_notifications_guard;
                UPDATE notifications SET created_at = now() - interval '13 hours' WHERE id = @old;
                ALTER TABLE notifications ENABLE TRIGGER trg_notifications_guard;
                """,
                new { read, old });
        }

        await PushAsync(presale);
        await PushAsync(read);
        await PushAsync(old);
        Assert.Empty(_expo.Sent);

        // Expo says the phone is gone: it is switched off, and nothing more is queued for Ana.
        _expo.SendError = "DeviceNotRegistered";
        var gone = await NotifyAsync("ana", "ticket_delivered", new { event_name = "Geese" });
        await PushAsync(gone);
        Assert.Equal("DeviceNotRegistered", await ScalarAsync<string>("SELECT disabled_reason FROM push_devices WHERE token = @AnaPhone", new { AnaPhone }));
        Assert.Equal("failed", await ScalarAsync<string>("SELECT status FROM push_deliveries WHERE notification_id = @gone", new { gone }));
        var next = await NotifyAsync("ana", "ticket_delivered", new { event_name = "Later" });
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE payload->>'notification_id' = @id", new { id = next.ToString() }));

        // Registering again (the app does on every launch) switches it back on.
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        Assert.Null(await ScalarAsync<string?>("SELECT disabled_reason FROM push_devices WHERE token = @AnaPhone", new { AnaPhone }));
    }

    [Fact]
    public async Task Receipts_mark_pushes_delivered_or_failed_and_switch_off_unregistered_devices()
    {
        var ana = await SignInAsync("ana");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaTablet, platform = "android" }), HttpStatusCode.OK);
        var id = await NotifyAsync("ana", "ticket_delivered", new { event_name = "Geese" });
        await PushAsync(id);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE push_deliveries SET sent_at = now() - interval '20 minutes'");
        }

        var tickets = (await QueryListAsync<string>("SELECT d.ticket_id FROM push_deliveries d JOIN push_devices p ON p.id = d.device_id ORDER BY p.token")).ToList();
        _expo.Receipts[tickets[0]] = "ok";                    // AnaPhone
        _expo.Receipts[tickets[1]] = "DeviceNotRegistered";   // AnaTablet

        var job = new PushReceiptsJob(_db, Client(), Options(), NullLogger<PushReceiptsJob>.Instance);
        Assert.Equal(("notifications.push_receipts", TimeSpan.FromMinutes(15)), (job.Kind, job.Every));
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, "{}", 1, 10), CancellationToken.None);

        Assert.Equal(["delivered", "failed"], await QueryListAsync<string>(
            "SELECT d.status FROM push_deliveries d JOIN push_devices p ON p.id = d.device_id ORDER BY p.token"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM push_devices WHERE disabled_at IS NOT NULL AND token = @AnaTablet", new { AnaTablet }));
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_devices_and_a_fan_without_one_queues_nothing()
    {
        var ana = await SignInAsync("ana");
        await NotifyAsync("ana", "ticket_delivered", new { event_name = "Geese" });
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'notifications.push'"));
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = AnaPhone, platform = "ios" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Delete, "/v1/me", ana), HttpStatusCode.Accepted);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM push_devices"));
    }

    private async Task<Guid> NotifyAsync(string handle, string kind, object data)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO notifications (user_id, kind, subject_id, data) SELECT id, @kind, gen_random_uuid(), @data::jsonb FROM users WHERE handle = @handle RETURNING id",
            new { handle, kind, data = JsonSerializer.Serialize(data) });
    }

    private async Task PushAsync(Guid notificationId)
    {
        var job = new PushNotificationJob(_db, Client(), Options(), TimeProvider.System, NullLogger<PushNotificationJob>.Instance);
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, $$"""{"notification_id":"{{notificationId}}"}""", 1, 10), CancellationToken.None);
    }

    private ExpoPushClient Client() => new(new HttpClient(_expo, disposeHandler: false), Options());

    private static Microsoft.Extensions.Options.IOptions<PushOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PushOptions());

    private async Task<List<T>> QueryListAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<T>(sql)];
    }

    /// <summary>Expo's push API: records every message, answers ok (or <see cref="SendError"/>) and the receipts set.</summary>
    private sealed class FakeExpo : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<JsonElement> Sent { get; } = new();

        public string? SendError { get; set; }

        public Dictionary<string, string> Receipts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            object answer;
            if (request.RequestUri!.AbsolutePath.EndsWith("/getReceipts", StringComparison.Ordinal))
            {
                answer = new
                {
                    data = body.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!).Where(Receipts.ContainsKey)
                        .ToDictionary(i => i, i => Receipts[i] == "ok"
                            ? (object)new { status = "ok" }
                            : new { status = "error", message = "gone", details = new { error = Receipts[i] } }),
                };
            }
            else
            {
                var results = new List<object>();
                foreach (var message in body.EnumerateArray())
                {
                    Sent.Enqueue(message);
                    results.Add(SendError is null
                        ? new { status = "ok", id = $"ticket-{Guid.NewGuid():N}" }
                        : new { status = "error", message = "gone", details = new { error = SendError } });
                }

                answer = new { data = results };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(answer), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<string> StaffAsync(string handle)
    {
        var token = await SignInAsync(handle);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        return token;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
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
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}
