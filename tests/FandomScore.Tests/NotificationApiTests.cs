using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>In-app notifications (migration 0021): written by the draw, reports, and the worker; read through the API.</summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Chicago = ["us-il-chicago"];
    private static readonly string[] Toronto = ["ca-on-toronto"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _allocationId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"notifications_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, venue, city_id, starts_at) VALUES (@_artistId, 'Geese at Metro', 'Metro', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        _allocationId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, 1, 4500, 10, now() - interval '1 hour', now() + interval '2 seconds', now()) RETURNING id
            """,
            new { eventId });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Fans_and_staff_are_told_what_happened_and_can_mark_it_read()
    {
        var ana = await FanAsync("ana", 90);
        var ben = await FanAsync("ben", 80);
        var staff = await StaffAsync("staffer");
        await Task.Delay(TimeSpan.FromSeconds(2.5));   // registration closes
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{_allocationId}/selection", staff), HttpStatusCode.Created);

        var anaPage = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ana), HttpStatusCode.OK);
        var won = Assert.Single(anaPage.GetProperty("items").EnumerateArray());
        Assert.Equal(("draw_selected", "You got tickets!", "/tickets", false), (won.GetProperty("kind").GetString(), won.GetProperty("title").GetString(),
            won.GetProperty("link").GetString(), won.GetProperty("read").GetBoolean()));
        Assert.Contains("Geese at Metro", won.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, anaPage.GetProperty("unread_count").GetInt32());

        var benPage = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ben), HttpStatusCode.OK);
        Assert.Equal("draw_not_selected", Assert.Single(benPage.GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());

        var staffPage = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", staff), HttpStatusCode.OK);
        var draw = Assert.Single(staffPage.GetProperty("items").EnumerateArray());
        Assert.Equal("staff_draw", draw.GetProperty("kind").GetString());
        Assert.Equal("Geese at Metro: 1 of 2 fans selected, 1 of 1 seats assigned.", draw.GetProperty("body").GetString());

        // Ana returns her seat: Ben is told one opened up.
        var reservationId = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", ana), HttpStatusCode.OK)).GetProperty("items")[0]
            .GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{reservationId}/return", ana), HttpStatusCode.OK);
        benPage = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ben), HttpStatusCode.OK);
        Assert.Equal(("seat_backfilled", 2), (benPage.GetProperty("items")[0].GetProperty("kind").GetString(), benPage.GetProperty("unread_count").GetInt32()));

        // Delivery time passes; the worker announces Ben's ticket once.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE reservations DISABLE TRIGGER trg_reservation_guard; UPDATE reservations SET deliver_at = now() - interval '1 minute'; ALTER TABLE reservations ENABLE TRIGGER trg_reservation_guard;");
        }

        var job = new TicketDeliveredJob(_db, NullLogger<TicketDeliveredJob>.Instance);
        Assert.Equal(TimeSpan.FromMinutes(15), job.Every);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);
        benPage = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ben), HttpStatusCode.OK);
        Assert.Equal(["ticket_delivered", "seat_backfilled", "draw_not_selected"],
            benPage.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));

        // Marking read: one, someone else's (ignored), then all.
        var newest = benPage.GetProperty("items")[0].GetProperty("id").GetString();
        var afterOne = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/notifications/read", ben, new { ids = new[] { newest } }), HttpStatusCode.OK);
        Assert.Equal(2, afterOne.GetProperty("unread_count").GetInt32());
        var wonId = won.GetProperty("id").GetString();
        Assert.Equal(2, (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/notifications/read", ben, new { ids = new[] { wonId } }), HttpStatusCode.OK))
            .GetProperty("unread_count").GetInt32());
        Assert.Equal(1, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ana), HttpStatusCode.OK)).GetProperty("unread_count").GetInt32());
        Assert.Equal(0, (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/notifications/read", ben, new { }), HttpStatusCode.OK))
            .GetProperty("unread_count").GetInt32());
        Assert.True((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications?limit=1", ben), HttpStatusCode.OK))
            .GetProperty("items")[0].GetProperty("read").GetBoolean());

        var tooMany = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/notifications/read", ben,
            new { ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray() }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("ids", tooMany.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Fans_with_a_score_in_the_show_city_hear_about_a_presale_once_unless_they_turned_it_off()
    {
        var ana = await FanAsync("ana", 90);
        var ben = await FanAsync("ben", 80);
        var cara = await SignInAsync("cara");   // Chicago, but no score for Geese
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", cara, new { city_ids = Chicago }), HttpStatusCode.OK);
        var dan = await FanAsync("dan", 70);    // a score, but Toronto
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", dan, new { city_ids = Toronto }), HttpStatusCode.OK);

        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", ben), HttpStatusCode.OK);
        Assert.True(me.GetProperty("presale_reminders").GetBoolean());
        var off = await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", ben, new { presale_reminders = false }), HttpStatusCode.OK);
        Assert.False(off.GetProperty("presale_reminders").GetBoolean());
        await JsonAsync(await SendAsync(HttpMethod.Patch, "/v1/me", ben, new { presale_reminders = "no" }), HttpStatusCode.UnprocessableEntity);

        Guid eventId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            eventId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM events WHERE name = 'Geese at Metro'");
            await connection.ExecuteAsync(
                """
                INSERT INTO event_presales (event_id, name, starts_at, ends_at) VALUES
                  (@eventId, 'Verified Fan Presale', now() + interval '5 hours', now() + interval '2 days'),
                  (@eventId, 'Spotify Presale', now() + interval '3 days', NULL),
                  (@eventId, 'Old Presale', now() - interval '3 days', now() - interval '1 day');
                INSERT INTO events (artist_id, name, city_id, starts_at, status)
                VALUES (@_artistId, 'Cancelled show', 'us-il-chicago', now() + interval '30 days', 'cancelled');
                INSERT INTO event_presales (event_id, name, starts_at)
                SELECT id, 'Fan Club Presale', now() + interval '1 hour' FROM events WHERE name = 'Cancelled show';
                """,
                new { eventId, _artistId });
        }

        var job = new PresaleReminderJob(_db, NullLogger<PresaleReminderJob>.Instance);
        Assert.Equal(("notifications.presales", TimeSpan.FromMinutes(15)), (job.Kind, job.Every));
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);

        // A sync replaces the presale rows; the fan still hears about each presale once.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                CREATE TEMP TABLE kept AS SELECT * FROM event_presales WHERE event_id = @eventId AND name = 'Verified Fan Presale';
                DELETE FROM event_presales WHERE event_id = @eventId AND name = 'Verified Fan Presale';
                INSERT INTO event_presales SELECT * FROM kept;
                DROP TABLE kept;
                """,
                new { eventId });
        }

        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);

        var anaItems = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", ana), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().ToList();
        var presale = Assert.Single(anaItems);
        Assert.Equal(("presale", "Presale: Geese", $"/show/{eventId}"), (presale.GetProperty("kind").GetString(),
            presale.GetProperty("title").GetString(), presale.GetProperty("link").GetString()));
        Assert.Equal("Verified Fan Presale for Geese at Metro starts in about 5 hours.", presale.GetProperty("body").GetString());
        foreach (var other in new[] { ben, cara, dan })
        {
            Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", other), HttpStatusCode.OK))
                .GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public void Presale_times_read_relative_to_now()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        static JsonElement Data(string starts, string? ends = null) =>
            JsonDocument.Parse(JsonSerializer.Serialize(new { starts_at = starts, ends_at = ends })).RootElement;
        Assert.Equal("starts within the hour", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-26T12:30:00Z"), now));
        Assert.Equal("starts in about an hour", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-26T13:10:00Z"), now));
        Assert.Equal("starts in about 22 hours", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-27T10:00:00Z"), now));
        Assert.Equal("starts in 3 days", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-29T12:00:00Z"), now));
        Assert.Equal("is on now", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-26T10:00:00Z", "2026-10-27T10:00:00Z"), now));
        Assert.Equal("has ended", FandomScore.Notifications.NotificationText.PresaleWhen(Data("2026-10-25T10:00:00Z", "2026-10-26T10:00:00Z"), now));
    }

    private async Task<string> FanAsync(string handle, decimal score)
    {
        var token = await SignInAsync(handle);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = Chicago }), HttpStatusCode.OK);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, @score, least(@score, 100), false, 0.1 FROM users WHERE handle = @handle
                """,
                new { _artistId, score, handle });
        }

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", token), HttpStatusCode.Created);
        return token;
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
