using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Allocations;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Allocations under load (services/api/src/features/allocations/TODO.md): the same request sent many times at once,
/// registrations racing each other and the draw, two draws at once, repeating a draw from its stored seed, and the
/// standing snapshot that keeps the show page cheap during an on-sale. Also the city time zones (migration 0033).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AllocationConcurrencyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _eventId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"alloc_concurrency_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                // Only the standing snapshot reads this clock, so its age can be moved by hand.
                services.Replace(ServiceDescriptor.Singleton<StandingSnapshots>(sp => new StandingSnapshots(
                    sp.GetRequiredService<NpgsqlDataSource>(), _clock, sp.GetRequiredService<IConfiguration>())));
            });
        });
        _client = _factory.CreateClient();

        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-load", "Load Test", releases: 1);
        _eventId = await ShowAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task The_same_registration_sent_twenty_times_at_once_registers_the_fan_once()
    {
        var allocationId = await AllocationAsync(_eventId);
        var fan = await FanAsync("dup_fan", score: 60);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan, new { plus_one = true })));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        var ids = await Task.WhenAll(responses.Select(async r => (await JsonAsync(r, r.StatusCode)).GetProperty("id").GetGuid()));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await LiveRegistrationsAsync(allocationId, "dup_fan"));
        Assert.True(await ScalarAsync<bool>("SELECT plus_one FROM allocation_registrations WHERE id = @id", new { id = ids[0] }));
    }

    [Fact]
    public async Task Racing_registrations_for_two_allocations_of_one_show_leave_one()
    {
        var first = await AllocationAsync(_eventId);
        var second = await AllocationAsync(_eventId);
        for (var round = 0; round < 10; round++)
        {
            var fan = await FanAsync($"race_{round}", score: 60);
            var responses = await Task.WhenAll(
                SendAsync(HttpMethod.Put, $"/v1/allocations/{first}/registration", fan),
                SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", fan));

            var codes = responses.Select(r => r.StatusCode).OrderBy(c => c).ToArray();
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], codes);
            var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal("already_registered", (await JsonAsync(refused, HttpStatusCode.Conflict)).GetProperty("code").GetString());
            Assert.Equal(1, await ScalarAsync<int>(
                "SELECT count(*)::int FROM allocation_registrations r JOIN users u ON u.id = r.user_id WHERE u.handle = @handle AND r.withdrawn_at IS NULL",
                new { handle = $"race_{round}" }));
        }
    }

    [Fact]
    public async Task The_draw_waits_for_a_registration_still_in_flight_and_counts_it()
    {
        // Registration closes in three seconds; a fan registers just before, in a transaction that stays open.
        await FanAsync("in_flight", score: 70);
        var fanId = await ScalarAsync<Guid>("SELECT id FROM users WHERE handle = 'in_flight'");
        var allocationId = await AllocationAsync(_eventId, closesInSeconds: 3);

        await using var registering = await _db.OpenConnectionAsync();
        await using var transaction = await registering.BeginTransactionAsync();
        await registering.ExecuteAsync("SELECT register_for_allocation(@fanId, @allocationId)", new { fanId, allocationId }, transaction);

        var closes = await ScalarAsync<DateTime>("SELECT registration_closes_at FROM allocations WHERE id = @allocationId", new { allocationId });
        var wait = closes - DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        var draw = Task.Run(async () =>
        {
            await using var connection = await _db.OpenConnectionAsync();
            return await connection.QuerySingleAsync<(int Registrations, int SelectedFans)>(
                "SELECT registrations, selected_fans FROM run_allocation_selection(@allocationId, NULL)", new { allocationId });
        });
        await Task.Delay(700);
        Assert.False(draw.IsCompleted, "the draw must wait for the registration still holding the allocation");

        await transaction.CommitAsync();
        var result = await draw.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((1, 1), result);
        Assert.Equal("selected", await ScalarAsync<string>(
            "SELECT outcome FROM allocation_registrations WHERE allocation_id = @allocationId AND user_id = @fanId", new { allocationId, fanId }));
    }

    [Fact]
    public async Task Two_draws_at_once_record_one_draw_that_the_stored_seed_repeats()
    {
        var allocationId = await AllocationAsync(_eventId, tickets: 5);
        // Ties on purpose: several fans share a score, so the seed decides their order.
        for (var i = 0; i < 12; i++)
        {
            var fan = await FanAsync($"seed_{i}", score: i < 8 ? 50 : 40 + i, i % 3 == 0 ? "ca-on-toronto" : "us-il-chicago");
            await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan,
                new { plus_one = i == 9 }), HttpStatusCode.Created);
        }

        await CloseRegistrationAsync(allocationId);
        var seeds = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var connection = await _db.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<string>("SELECT seed FROM run_allocation_selection(@allocationId, NULL)", new { allocationId });
        })));

        Assert.Single(seeds.Distinct());
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM allocation_selections WHERE allocation_id = @allocationId", new { allocationId }));

        // Anyone can repeat the draw: the ranking from the stored seed matches every stored rank.
        var mismatches = await ScalarAsync<int>(
            """
            SELECT count(*)::int
            FROM allocation_registrations r
            JOIN allocation_ranking(@allocationId, (SELECT seed FROM allocation_selections WHERE allocation_id = @allocationId)) k
              ON k.registration_id = r.id
            WHERE r.selection_rank IS DISTINCT FROM k.rank
            """,
            new { allocationId });
        Assert.Equal(0, mismatches);
        Assert.Equal(12, await ScalarAsync<int>(
            "SELECT count(*)::int FROM allocation_registrations WHERE allocation_id = @allocationId AND selection_rank IS NOT NULL", new { allocationId }));

        // A different seed may reorder the tied fans but never the score order or local fans first.
        var outOfOrder = await ScalarAsync<int>(
            """
            WITH k AS (
              SELECT k.rank, r.hub_match IS NULL AS away, r.raw_score
              FROM allocation_ranking(@allocationId, 'another seed') k JOIN allocation_registrations r ON r.id = k.registration_id
            )
            SELECT count(*)::int FROM k a JOIN k b ON b.rank = a.rank + 1
            WHERE (a.away, -a.raw_score) > (b.away, -b.raw_score)
            """,
            new { allocationId });
        Assert.Equal(0, outOfOrder);
    }

    [Fact]
    public async Task Standing_comes_from_a_snapshot_refreshed_after_a_few_seconds()
    {
        var allocationId = await AllocationAsync(_eventId, tickets: 100);
        var a = await FanAsync("snap_a", score: 80);
        var b = await FanAsync("snap_b", score: 70);
        var c = await FanAsync("snap_c", score: 90);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", a), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", b), HttpStatusCode.Created);

        Assert.Equal((2, 2), await RankAsync(b));

        // c registers ahead of b; within the snapshot's lifetime b's standing doesn't see it yet.
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", c), HttpStatusCode.Created);
        Assert.Equal((2, 2), await RankAsync(b));
        // c's own standing is right even though the snapshot predates c (c is the fan asking).
        Assert.Equal((1, 3), await RankAsync(c));

        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal((3, 3), await RankAsync(b));
        Assert.Equal((1, 3), await RankAsync(c));

        async Task<(int Rank, int Registrants)> RankAsync(string token)
        {
            var standing = (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", token),
                HttpStatusCode.OK)).GetProperty("standing");
            return (standing.GetProperty("rank").GetInt32(), standing.GetProperty("registrants").GetInt32());
        }
    }

    [Fact]
    public void The_snapshot_ranks_exactly_like_counting_every_registration()
    {
        var random = new Random(20261026);
        for (var trial = 0; trial < 200; trial++)
        {
            var entries = Enumerable.Range(0, random.Next(0, 60))
                .Select(_ => new StandingSnapshots.Entry(Guid.NewGuid(), random.Next(2) == 0, random.Next(0, 12) * 2.5m, random.Next(1, 3)))
                .ToList();
            var snapshot = new StandingSnapshots.Snapshot(entries);

            // Someone in the snapshot (by their own entry), and someone who isn't, with every place and a range of scores.
            var askers = entries.Take(5).Select(e => (e.UserId, e.Local, e.RawScore)).ToList();
            askers.Add((Guid.NewGuid(), random.Next(2) == 0, random.Next(0, 12) * 2.5m));
            foreach (var (userId, local, score) in askers)
            {
                var others = entries.Where(e => e.UserId != userId).ToList();
                var ahead = others.Where(o => (o.Local && !local) || (o.Local == local && o.RawScore >= score)).ToList();
                Assert.Equal((others.Count, ahead.Count, ahead.Sum(o => o.Seats)), snapshot.Rank(userId, local, score));
            }
        }
    }

    [Fact]
    public async Task Cities_and_shows_carry_their_time_zone()
    {
        var cities = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/cities?q=El%20Paso", await FanAsync("tz_fan", score: null)), HttpStatusCode.OK);
        var elPaso = Assert.Single(cities.GetProperty("items").EnumerateArray());
        Assert.Equal("America/Denver", elPaso.GetProperty("time_zone").GetString());

        await AllocationAsync(_eventId);
        var show = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{_eventId}", await FanAsync("tz_fan_2", score: null)), HttpStatusCode.OK);
        Assert.Equal("America/Chicago", show.GetProperty("event").GetProperty("time_zone").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> ShowAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Load show', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
    }

    private async Task<Guid> AllocationAsync(Guid eventId, int tickets = 10, double closesInSeconds = 5 * 86400)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, @tickets, 4500, 0, now() - interval '1 hour', now() + make_interval(secs => @closesInSeconds), now())
            RETURNING id
            """,
            new { eventId, tickets, closesInSeconds });
    }

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

    private async Task<int> LiveRegistrationsAsync(Guid allocationId, string handle) => await ScalarAsync<int>(
        """
        SELECT count(*)::int FROM allocation_registrations r JOIN users u ON u.id = r.user_id
        WHERE r.allocation_id = @allocationId AND u.handle = @handle AND r.withdrawn_at IS NULL
        """,
        new { allocationId, handle });

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    /// <summary>A signed-in fan with hub cities (Chicago unless given) and, unless null, a score for the artist.</summary>
    private async Task<string> FanAsync(string handle, decimal? score, params string[] hubs)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var token = (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token,
            new { city_ids = hubs.Length > 0 ? hubs : ["us-il-chicago"] }), HttpStatusCode.OK);

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

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
