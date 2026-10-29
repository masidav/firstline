using FandomScore.Tests.Contract;
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
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Fan registration for allocations: eligibility, register, withdraw, and the snapshot (migration 0013); the +1, its
/// guest, and the draw (0017).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RegistrationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _eventId;
    private Guid _allocationId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"registrations_{Guid.NewGuid():N}");
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
        (_eventId, _allocationId) = await ShowWithAllocationAsync(minRawScore: 50);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task An_eligible_fan_registers_with_a_snapshot_of_hubs_and_score()
    {
        var fan = await FanAsync("local_fan", score: 91.2m, "us-ny-new-york", "us-il-chicago");

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", fan), HttpStatusCode.OK);
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, eligibility.GetProperty("reason").ValueKind);
        Assert.Equal((91.2m, 50m, 2), (eligibility.GetProperty("raw_score").GetDecimal(),
            eligibility.GetProperty("min_raw_score").GetDecimal(), eligibility.GetProperty("hub_match").GetInt32()));

        var response = await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan);
        var registration = await JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal($"/v1/allocations/{_allocationId}/registration", response.Headers.Location?.ToString());
        Assert.Equal(["us-ny-new-york", "us-il-chicago"], registration.GetProperty("hub_city_ids").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal((2, true, 91.2m, "registered"), (registration.GetProperty("hub_match").GetInt32(),
            registration.GetProperty("local").GetBoolean(), registration.GetProperty("percentile").GetDecimal(),
            registration.GetProperty("status").GetString()));
        Assert.False(registration.GetProperty("plus_one").GetBoolean());

        // Registering again returns the same registration.
        var again = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.OK);
        Assert.Equal(registration.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", fan), HttpStatusCode.OK);
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(_allocationId.ToString(), item.GetProperty("allocation").GetProperty("id").GetString());
        Assert.Equal("Chicago", item.GetProperty("event").GetProperty("city").GetString());
    }

    [Fact]
    public async Task A_fan_from_elsewhere_can_register_but_is_not_local()
    {
        var fan = await FanAsync("far_fan", score: 60, "ca-on-toronto");

        var registration = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan),
            HttpStatusCode.Created);

        Assert.False(registration.GetProperty("local").GetBoolean());
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("hub_match").ValueKind);
    }

    [Theory]
    [InlineData(40, "below_threshold", HttpStatusCode.Forbidden, "not_eligible")]
    [InlineData(null, "no_score", HttpStatusCode.Forbidden, "not_eligible")]
    public async Task Fans_below_the_threshold_or_without_a_score_are_told_why(int? score, string reason, HttpStatusCode status, string code)
    {
        var fan = await FanAsync("fan_x", score, "us-il-chicago");

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", fan), HttpStatusCode.OK);
        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), status);

        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(reason, eligibility.GetProperty("reason").GetString());
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_fan_without_a_hub_city_must_choose_one_first()
    {
        var fan = await FanAsync("no_hub", score: 99);

        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Conflict);

        Assert.Equal("hub_city_required", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_registration_per_show_until_withdrawn()
    {
        var fan = await FanAsync("one_show", score: 99, "us-il-chicago");
        var second = await AllocationAsync(_eventId, minRawScore: 0);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Created);

        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", fan), HttpStatusCode.Conflict);
        Assert.Equal("already_registered", problem.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan)).StatusCode);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", fan), HttpStatusCode.Created);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", fan), HttpStatusCode.OK);
        Assert.Equal(["registered", "withdrawn"], mine.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("registration").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Nobody_registers_before_opening_or_for_drafts()
    {
        var fan = await FanAsync("early", score: 99, "us-il-chicago");
        var notYet = await AllocationAsync(_eventId, minRawScore: 0, opensInHours: 24);
        var draft = await AllocationAsync(_eventId, minRawScore: 0, publish: false);

        var closed = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{notYet}/registration", fan), HttpStatusCode.Conflict);
        Assert.Equal("registration_closed", closed.GetProperty("code").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{draft}/registration", fan), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{draft}/eligibility", fan), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdrawing_after_registration_closes_is_refused()
    {
        var fan = await FanAsync("late", score: 99, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Created);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // Close the window directly (the API can't change an open allocation's rules, by design).
            await connection.ExecuteAsync(
                """
                ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
                UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour'
                 WHERE id = @_allocationId;
                ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
                """,
                new { _allocationId });
        }

        var problem = await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Conflict);

        Assert.Equal("registration_closed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_plus_one_can_be_asked_for_at_registration_and_changed_while_open()
    {
        var fan = await FanAsync("plus_fan", score: 80, "us-il-chicago");

        var registration = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan,
            new { plus_one = true }), HttpStatusCode.Created);
        Assert.True(registration.GetProperty("plus_one").GetBoolean());

        var off = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan,
            new { plus_one = false }), HttpStatusCode.OK);
        Assert.False(off.GetProperty("plus_one").GetBoolean());
        var unchanged = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.OK);
        Assert.False(unchanged.GetProperty("plus_one").GetBoolean());
    }

    [Fact]
    public async Task A_guest_needs_an_account_and_is_named_once()
    {
        var fan = await FanAsync("host_fan", score: 80, "us-il-chicago");
        await FanAsync("friend", score: null);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Created);

        var noPlusOne = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", fan,
            new { handle = "friend" }), HttpStatusCode.Conflict);
        Assert.Equal("guest_unavailable", noPlusOne.GetProperty("code").GetString());

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan, new { plus_one = true }), HttpStatusCode.OK);
        var unknown = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", fan,
            new { handle = "not_signed_up_yet" }), HttpStatusCode.Conflict);
        Assert.Equal("guest_unavailable", unknown.GetProperty("code").GetString());

        var named = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", fan,
            new { handle = "@friend" }), HttpStatusCode.OK);
        Assert.Equal("friend", named.GetProperty("guest").GetProperty("handle").GetString());

        await FanAsync("other_friend", score: null);
        var twice = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", fan,
            new { handle = "other_friend" }), HttpStatusCode.Conflict);
        Assert.Equal("guest_unavailable", twice.GetProperty("code").GetString());
        var dropping = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan,
            new { plus_one = false }), HttpStatusCode.Conflict);
        Assert.Equal("plus_one_unavailable", dropping.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_named_guest_is_told_can_decline_before_the_draw_and_then_register_and_cant_be_named_again()
    {
        var host = await FanAsync("host_fan", score: 80, "us-il-chicago");
        var rival = await FanAsync("rival_fan", score: 90, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", host, new { plus_one = true }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", host, new { handle = "rival_fan" }),
            HttpStatusCode.OK);

        // The rival hears about it, and registration says why it's blocked.
        var notices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", rival), HttpStatusCode.OK);
        var notice = Assert.Single(notices.GetProperty("items").EnumerateArray());
        Assert.Equal("guest_named", notice.GetProperty("kind").GetString());
        Assert.Contains("@host_fan named you as their +1", notice.GetProperty("body").GetString(), StringComparison.Ordinal);
        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", rival), HttpStatusCode.OK);
        Assert.Equal("named_as_guest", eligibility.GetProperty("reason").GetString());
        var blocked = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", rival), HttpStatusCode.Conflict);
        Assert.Equal("named_as_guest", blocked.GetProperty("code").GetString());

        // Declining frees them; declining again does nothing.
        var declined = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/events/{_eventId}/guest-invite/decline", rival), HttpStatusCode.OK);
        Assert.Equal(1, declined.GetProperty("declined").GetInt32());
        Assert.Equal(0, (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/events/{_eventId}/guest-invite/decline", rival), HttpStatusCode.OK))
            .GetProperty("declined").GetInt32());
        var hostRegistration = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", host), HttpStatusCode.OK)).GetProperty("items")[0];
        var registration = hostRegistration.TryGetProperty("registration", out var inner) ? inner : hostRegistration;
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("guest").ValueKind);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", rival), HttpStatusCode.Created);

        // The host can't name them again for this show.
        var again = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", host, new { handle = "rival_fan" }),
            HttpStatusCode.Conflict);
        Assert.Equal("guest_unavailable", again.GetProperty("code").GetString());
        Assert.Contains("declined", again.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_draw_ranks_local_fans_first_seats_plus_ones_and_records_a_seed()
    {
        var staff = await FanAsync("draw_staff", score: null);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'draw_staff'");
        }

        var allocationId = await AllocationAsync(_eventId, minRawScore: 0, tickets: 3);
        var local = await FanAsync("local_top", score: 70, "us-il-chicago");
        var far = await FanAsync("far_top", score: 99, "ca-on-toronto");
        var second = await FanAsync("local_second", score: 60, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", local, new { plus_one = true }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", far), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", second), HttpStatusCode.Created);

        var early = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.Conflict);
        Assert.Equal("selection_not_ready", early.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", local)).StatusCode);

        await CloseRegistrationAsync(allocationId);
        // Staff queue the draw; the worker runs it. Queuing again meanwhile changes nothing.
        var queued = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.Accepted);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        var stillQueued = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.Accepted);
        Assert.Equal("queued", stillQueued.GetProperty("status").GetString());
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM jobs WHERE kind = 'allocations.select'"));
        }

        await Draws.RunQueuedAsync(_db);
        var done = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.OK);
        Assert.Equal("done", done.GetProperty("status").GetString());
        var selection = done.GetProperty("selection");
        Assert.Equal((3, 3, 2, 3), (selection.GetProperty("tickets").GetInt32(), selection.GetProperty("registrations").GetInt32(),
            selection.GetProperty("selected_fans").GetInt32(), selection.GetProperty("seats_assigned").GetInt32()));
        var again = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.OK);
        Assert.Equal(selection.GetProperty("seed").GetString(), again.GetProperty("selection").GetProperty("seed").GetString());

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/allocations/{allocationId}/registrations", staff), HttpStatusCode.OK);
        Assert.Equal([("local_top", "selected", 2), ("local_second", "selected", 1), ("far_top", "not_selected", 0)],
            list.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("handle").GetString(),
                i.GetProperty("registration").GetProperty("status").GetString(),
                i.GetProperty("registration").GetProperty("seats").ValueKind == JsonValueKind.Null ? 0 : i.GetProperty("registration").GetProperty("seats").GetInt32())));

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", far), HttpStatusCode.OK);
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal("not_selected", item.GetProperty("registration").GetProperty("status").GetString());
        Assert.Equal("selected", item.GetProperty("allocation").GetProperty("status").GetString());
        Assert.Equal(0, item.GetProperty("allocation").GetProperty("seats_left").GetInt32());

        // No seat left for local_second's +1 after the draw.
        var full = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", second,
            new { plus_one = true }), HttpStatusCode.Conflict);
        Assert.Equal("plus_one_unavailable", full.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_selected_fan_can_add_a_plus_one_while_seats_are_left()
    {
        var staff = await FanAsync("draw_staff2", score: null);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'draw_staff2'");
        }

        var allocationId = await AllocationAsync(_eventId, minRawScore: 0, tickets: 5);
        var fan = await FanAsync("late_plus", score: 70, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan), HttpStatusCode.Created);
        await CloseRegistrationAsync(allocationId);
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);

        var added = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan,
            new { plus_one = true }), HttpStatusCode.OK);

        Assert.Equal((true, 2, "selected"), (added.GetProperty("plus_one").GetBoolean(), added.GetProperty("seats").GetInt32(),
            added.GetProperty("status").GetString()));
        await FanAsync("late_guest", score: null);
        var named = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration/guest", fan,
            new { handle = "late_guest" }), HttpStatusCode.OK);
        Assert.Equal("late_guest", named.GetProperty("guest").GetProperty("handle").GetString());
    }

    [Fact]
    public async Task Standing_says_likely_only_inside_the_tickets_minus_a_25_seat_margin()
    {
        // 28 tickets - 25 margin = 3 seats count as "likely".
        var allocationId = await AllocationAsync(_eventId, minRawScore: 0, tickets: 28);
        var top = await FanAsync("st_top", score: 90, "us-il-chicago");
        var next = await FanAsync("st_next", score: 80, "us-il-chicago");
        var far = await FanAsync("st_far", score: 99, "ca-on-toronto");
        var tied = await FanAsync("st_tied", score: 80, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", top, new { plus_one = true }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", next), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", far), HttpStatusCode.Created);

        async Task<JsonElement> StandingAsync(string token, string query = "") =>
            (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility{query}", token), HttpStatusCode.OK))
            .GetProperty("standing");

        var nextStanding = await StandingAsync(next);   // behind top's 2 seats: 2 + 1 = 3 fits
        Assert.Equal((2, 3, 67, 2, 1, true), (nextStanding.GetProperty("rank").GetInt32(), nextStanding.GetProperty("registrants").GetInt32(),
            nextStanding.GetProperty("top_percent").GetInt32(), nextStanding.GetProperty("seats_ahead").GetInt32(),
            nextStanding.GetProperty("seats_needed").GetInt32(), nextStanding.GetProperty("likely").GetBoolean()));
        Assert.Equal(25, nextStanding.GetProperty("margin_seats").GetInt32());

        var farStanding = await StandingAsync(far);      // highest score, but not local: behind 3 seats, 3 + 1 > 3
        Assert.Equal((3, false, false), (farStanding.GetProperty("rank").GetInt32(), farStanding.GetProperty("local").GetBoolean(),
            farStanding.GetProperty("likely").GetBoolean()));

        var tiedStanding = await StandingAsync(tied);    // not registered yet; the tie with next counts against them
        Assert.Equal((3, 4, 3, false), (tiedStanding.GetProperty("rank").GetInt32(), tiedStanding.GetProperty("registrants").GetInt32(),
            tiedStanding.GetProperty("seats_ahead").GetInt32(), tiedStanding.GetProperty("likely").GetBoolean()));
        var withPlusOne = await StandingAsync(tied, "?plus_one=true");
        Assert.Equal(2, withPlusOne.GetProperty("seats_needed").GetInt32());

        var low = await FanAsync("st_none", score: null, "us-il-chicago");
        Assert.Equal(JsonValueKind.Null, (await StandingAsync(low)).ValueKind);
    }

    // Moves the window into the past directly (the API can't change an open allocation's rules, by design).
    private async Task CloseRegistrationAsync(Guid allocationId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
            UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour'
             WHERE id = @allocationId;
            ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
            """,
            new { allocationId });
    }

    private async Task<(Guid EventId, Guid AllocationId)> ShowWithAllocationAsync(decimal minRawScore)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Geese', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        return (eventId, await AllocationAsync(eventId, minRawScore));
    }

    private async Task<Guid> AllocationAsync(Guid eventId, decimal minRawScore, double opensInHours = -1, bool publish = true, int tickets = 10)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, @tickets, 4500, @minRawScore, now() + make_interval(secs => @opens * 3600), now() + interval '5 days',
                    CASE WHEN @publish THEN now() END)
            RETURNING id
            """,
            new { eventId, minRawScore, opens = opensInHours, publish, tickets });
    }

    /// <summary>A signed-in fan with the given hub cities and, unless null, a score for the artist (raw score and
    /// percentile both set to <paramref name="score"/>).</summary>
    private async Task<string> FanAsync(string handle, decimal? score, params string[] hubs)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var token = (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
        if (hubs.Length > 0)
        {
            await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = hubs }), HttpStatusCode.OK);
        }

        if (score is not null)
        {
            await using var connection = await _db.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, @score, least(@score, 100), true, 0.9 FROM users WHERE handle = @handle
                """,
                new { _artistId, score, handle });
        }

        return token;
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
