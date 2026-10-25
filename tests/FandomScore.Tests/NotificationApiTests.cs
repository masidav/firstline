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
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
