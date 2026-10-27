using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using FandomScore.Database;
using FandomScore.Worker.Concerts;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Loading tracked artists' shows from Ticketmaster (migration 0030, workers/concerts) against a real database and a
/// fake Discovery API that answers in Ticketmaster's JSON shapes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcertSyncJobTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string GeeseSpotify = "2sSGPbdZJkaSE2AbcGOACx";
    private const string GeeseAttraction = "K8vZ917Geese";
    private readonly FakeTicketmaster _tm = new();
    private NpgsqlDataSource _db = null!;
    private Guid _geese;
    private int _runs;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"concerts_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _geese = await ScoringSetup.ActiveArtistAsync(_db, GeeseSpotify, "Geese", releases: 1);
        _tm.Attractions.Add(AttractionJson("K8vZ917Other", "Geese", "0000000000000000000000"));   // same name, another band
        _tm.Attractions.Add(AttractionJson(GeeseAttraction, "Geese", GeeseSpotify));
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose() => _tm.Dispose();

    [Fact]
    public async Task An_artist_is_linked_by_its_Spotify_page_and_its_shows_load_with_sales_and_presales()
    {
        _tm.Events[GeeseAttraction] =
        [
            Event("ev-chicago", "Geese", "2026-12-05T01:00:00Z", "Metro", "Chicago", "IL", "US", status: "onsale",
                publicSale: ("2026-11-01T15:00:00Z", "2026-12-05T01:00:00Z"),
                presales: [("Verified Fan Presale", "2026-10-28T15:00:00Z", "2026-10-30T03:00:00Z"), ("Spotify Presale", "2026-10-29T15:00:00Z", null)]),
            Event("ev-toronto", "Geese with Friends", null, "Danforth Music Hall", "Toronto", "ON", "CA", localDate: "2026-12-10",
                localTime: "19:30:00", timezone: "America/Toronto"),
            Event("ev-rosemont", "Geese", null, "Rosemont Theatre", "Rosemont", "IL", "US", localDate: "2027-01-15",
                timezone: "America/Chicago", timeTba: true, status: "cancelled"),
            Event("ev-london", "Geese", "2027-02-01T20:00:00Z", "Electric Ballroom", "London", null, "GB"),
        ];

        await SyncAsync();

        var link = await QueryOneAsync<(string ExternalId, string MatchedBy)>(
            "SELECT external_id, matched_by FROM artist_sources WHERE artist_id = @_geese AND source = 'ticketmaster'");
        Assert.Equal((GeeseAttraction, "spotify_link"), link);

        var shows = (await QueryAsync<ShowRow>(
            """
            SELECT external_id AS ExternalId, name AS Name, venue AS Venue, city_id AS CityId, region AS Region, status AS Status,
                   time_tba AS TimeTba, starts_at AS StartsAt, ticket_url AS TicketUrl, public_sale_starts_at AS PublicSaleStartsAt
            FROM events WHERE artist_id = @_geese ORDER BY starts_at
            """)).ToList();
        Assert.Equal(["ev-chicago", "ev-toronto", "ev-rosemont"], shows.Select(s => s.ExternalId));   // GB isn't kept
        var chicago = shows[0];
        Assert.Equal(("us-il-chicago", "onsale", new DateTime(2026, 12, 5, 1, 0, 0, DateTimeKind.Utc)), (chicago.CityId, chicago.Status, chicago.StartsAt));
        Assert.Equal("https://www.ticketmaster.com/event/ev-chicago", chicago.TicketUrl);
        Assert.Equal(new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc), chicago.PublicSaleStartsAt);
        // Only a local date and time: converted with the venue's time zone (19:30 in Toronto is 00:30 UTC in December).
        Assert.Equal(("ca-on-toronto", new DateTime(2026, 12, 11, 0, 30, 0, DateTimeKind.Utc)), (shows[1].CityId, shows[1].StartsAt));
        // A suburb has no hub city; a date without a time is local midnight and marked TBA.
        Assert.Equal((null, "cancelled", true, new DateTime(2027, 1, 15, 6, 0, 0, DateTimeKind.Utc)),
            (shows[2].CityId, shows[2].Status, shows[2].TimeTba, shows[2].StartsAt));
        Assert.Equal(2, await CountAsync(
            "SELECT count(*) FROM event_presales p JOIN events e ON e.id = p.event_id WHERE e.external_id = 'ev-chicago'"));

        // Again, with changes: one presale fewer, a new status. Nothing duplicates.
        _tm.Events[GeeseAttraction][0] = Event("ev-chicago", "Geese", "2026-12-05T01:00:00Z", "Metro", "Chicago", "IL", "US", status: "offsale",
            presales: [("Spotify Presale", "2026-10-29T15:00:00Z", null)]);
        await SyncAsync();
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM events WHERE artist_id = @_geese", new { _geese }));
        Assert.Equal("offsale", await QueryOneAsync<string>("SELECT status FROM events WHERE external_id = 'ev-chicago'"));
        Assert.Equal(["Spotify Presale"], await QueryAsync<string>(
            "SELECT p.name FROM event_presales p JOIN events e ON e.id = p.event_id WHERE e.external_id = 'ev-chicago'"));
        Assert.Equal(1, _tm.Calls.Count(c => c.StartsWith("/discovery/v2/attractions.json", StringComparison.Ordinal)));
        Assert.All(_tm.Calls, c => Assert.Contains("apikey=test-key", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_Spotify_match_means_no_link_and_no_new_search_for_a_week()
    {
        _tm.Attractions.RemoveAt(1);

        await SyncAsync();
        await SyncAsync();

        var link = await QueryOneAsync<(string? ExternalId, string? MatchedBy)>(
            "SELECT external_id, matched_by FROM artist_sources WHERE artist_id = @_geese");
        Assert.Equal((null, null), link);
        Assert.Single(_tm.Calls);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM events"));
    }

    [Fact]
    public async Task A_show_with_an_allocation_keeps_its_locked_time_and_city_and_a_co_headliners_show_is_left_alone()
    {
        _tm.Events[GeeseAttraction] = [Event("ev-chicago", "Geese", "2026-12-05T01:00:00Z", "Metro", "Chicago", "IL", "US")];
        await SyncAsync();
        var other = await ScoringSetup.ActiveArtistAsync(_db, "1111111111111111111111", "Other Band", releases: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO users (handle, email, is_staff) VALUES ('staffer', 'staffer@example.test', true)");
        }
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at)
                SELECT id, 10, 3000, 0, now() + interval '1 day', now() + interval '2 days' FROM events WHERE external_id = 'ev-chicago';
                INSERT INTO events (artist_id, name, starts_at, external_source, external_id)
                VALUES (@other, 'Other Band + Geese', '2026-12-20T02:00:00Z', 'ticketmaster', 'ev-shared');
                """,
                new { other });
        }

        _tm.Events[GeeseAttraction] =
        [
            Event("ev-chicago", "Geese", "2026-12-06T01:00:00Z", "Metro", "Chicago", "IL", "US", status: "rescheduled"),
            Event("ev-shared", "Other Band + Geese", "2026-12-20T02:00:00Z", "Riviera", "Chicago", "IL", "US"),
        ];
        await SyncAsync();

        var chicago = await QueryOneAsync<(DateTime StartsAt, DateTime SourceStartsAt, string Status)>(
            "SELECT starts_at, source_starts_at, status FROM events WHERE external_id = 'ev-chicago'");
        Assert.Equal((new DateTime(2026, 12, 5, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 6, 1, 0, 0, DateTimeKind.Utc), "rescheduled"), chicago);
        Assert.Equal(other, await QueryOneAsync<Guid>("SELECT artist_id FROM events WHERE external_id = 'ev-shared'"));

        // Staff hear about the move once (migration 0036), and again when Ticketmaster cancels the show.
        const string Notices = "SELECT data->>'status' FROM notifications WHERE kind = 'staff_show_changed' ORDER BY created_at";
        Assert.Equal(["rescheduled"], await QueryAsync<string>(Notices));
        await SyncAsync();
        Assert.Equal(["rescheduled"], await QueryAsync<string>(Notices));
        _tm.Events[GeeseAttraction][0] = Event("ev-chicago", "Geese", "2026-12-06T01:00:00Z", "Metro", "Chicago", "IL", "US", status: "cancelled");
        await SyncAsync();
        Assert.Equal(["rescheduled", "cancelled"], await QueryAsync<string>(Notices));
        var data = await QueryOneAsync<string>("SELECT data::text FROM notifications WHERE kind = 'staff_show_changed' AND data->>'status' = 'rescheduled'");
        using var json = System.Text.Json.JsonDocument.Parse(data);
        var message = FandomScore.Notifications.NotificationText.Describe("staff_show_changed", json.RootElement, DateTime.UtcNow);
        Assert.Equal(("A show with fan tickets changed",
            "Ticketmaster moved Geese to Dec 6, 01:00 UTC. Its fan allocation still uses Dec 5, 01:00 UTC: update the fans or the allocation."),
            (message.Title, message.Body));
        Assert.Null(await QueryOneAsync<string?>("SELECT venue FROM events WHERE external_id = 'ev-shared'"));
    }

    [Fact]
    public async Task Without_a_key_nothing_is_called_and_a_rate_limit_is_retried_later()
    {
        await SyncAsync(apiKey: "");
        Assert.Empty(_tm.Calls);

        _tm.Status = HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<TicketmasterUnavailableException>(() => SyncAsync());
    }

    [Fact]
    public async Task The_hourly_schedule_queues_artists_that_are_due_up_to_the_limit()
    {
        var fresh = await ScoringSetup.ActiveArtistAsync(_db, "2222222222222222222222", "Fresh", releases: 1);
        var unmatched = await ScoringSetup.ActiveArtistAsync(_db, "3333333333333333333333", "Unmatched", releases: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM jobs;
                INSERT INTO artist_sources (artist_id, source, external_id, matched_by, checked_at, synced_at)
                VALUES (@fresh, 'ticketmaster', 'K-fresh', 'staff', now(), now()),
                       (@unmatched, 'ticketmaster', NULL, NULL, now() - interval '2 days', NULL);
                """,
                new { fresh, unmatched });
        }

        var schedule = new ScheduleConcertSyncsJob(_db, Options(maxPerHour: 5), NullLogger<ScheduleConcertSyncsJob>.Instance);
        Assert.Equal(("concerts.schedule", TimeSpan.FromHours(1)), (schedule.Kind, schedule.Every));
        await schedule.RunAsync(new ClaimedJob(++_runs, schedule.Kind, "{}", 1, 10), CancellationToken.None);

        // Geese was never looked up; Fresh synced just now; Unmatched was checked 2 days ago (rechecked after 7).
        Assert.Equal([_geese.ToString()], await QueryAsync<string>("SELECT payload->>'artist_id' FROM jobs WHERE kind = 'concerts.sync_artist'"));

        var limited = new ScheduleConcertSyncsJob(_db, Options(maxPerHour: 0), NullLogger<ScheduleConcertSyncsJob>.Instance);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("DELETE FROM jobs");
        }

        await limited.RunAsync(new ClaimedJob(++_runs, limited.Kind, "{}", 1, 10), CancellationToken.None);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM jobs"));
    }

    [Fact]
    public void Spotify_artist_links_are_read_from_any_open_spotify_url()
    {
        Assert.Equal(GeeseSpotify, TicketmasterClient.SpotifyArtistId($"https://open.spotify.com/artist/{GeeseSpotify}?si=abc"));
        Assert.Equal(GeeseSpotify, TicketmasterClient.SpotifyArtistId($"https://open.spotify.com/intl-de/artist/{GeeseSpotify}"));
        Assert.Null(TicketmasterClient.SpotifyArtistId("https://open.spotify.com/album/2sSGPbdZJkaSE2AbcGOACx"));
        Assert.Null(TicketmasterClient.SpotifyArtistId("https://example.com/artist/2sSGPbdZJkaSE2AbcGOACx"));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record ShowRow(
        string ExternalId, string Name, string? Venue, string? CityId, string? Region, string Status, bool TimeTba, DateTime StartsAt,
        string? TicketUrl, DateTime? PublicSaleStartsAt);

    private async Task SyncAsync(string apiKey = "test-key")
    {
        var options = Options(apiKey: apiKey);
        var client = new TicketmasterClient(new HttpClient(_tm), options);
        var job = new ConcertSyncJob(_db, client, options, TimeProvider.System, NullLogger<ConcertSyncJob>.Instance);
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, $$"""{"artist_id":"{{_geese}}"}""", 1, 10), CancellationToken.None);
    }

    private static IOptions<TicketmasterOptions> Options(string apiKey = "test-key", int maxPerHour = 100) =>
        Microsoft.Extensions.Options.Options.Create(new TicketmasterOptions { ApiKey = apiKey, MaxSyncsPerHour = maxPerHour });

    private async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, args ?? new { _geese });
    }

    private async Task<T> QueryOneAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<T>(sql, new { _geese });
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QueryAsync<T>(sql, new { _geese });
    }

    private static object AttractionJson(string id, string name, string spotifyId) => new
    {
        id, name, type = "attraction",
        externalLinks = new { spotify = new[] { new { url = $"https://open.spotify.com/artist/{spotifyId}" } } },
    };

    private static object Event(
        string id, string name, string? dateTime, string venue, string city, string? state, string country,
        string status = "onsale", string? localDate = null, string? localTime = null, string? timezone = null, bool timeTba = false,
        (string Start, string End)? publicSale = null, (string Name, string Start, string? End)[]? presales = null) => new
    {
        id, name, type = "event", url = $"https://www.ticketmaster.com/event/{id}",
        dates = new
        {
            start = new { dateTime, localDate = localDate ?? dateTime?[..10], localTime, timeTBA = timeTba, noSpecificTime = false },
            timezone, status = new { code = status },
        },
        sales = new
        {
            @public = publicSale is { } p ? new { startDateTime = p.Start, endDateTime = p.End } : null,
            presales = (presales ?? []).Select(x => new { name = x.Name, startDateTime = x.Start, endDateTime = x.End }).ToArray(),
        },
        _embedded = new
        {
            venues = new[]
            {
                new
                {
                    name = venue, city = new { name = city }, state = state is null ? null : new { stateCode = state },
                    country = new { countryCode = country },
                },
            },
            attractions = new[] { new { id = GeeseAttraction } },
        },
    };

    /// <summary>The Discovery API: attraction search and an attraction's events, one page each.</summary>
    private sealed class FakeTicketmaster : HttpMessageHandler
    {
        public List<object> Attractions { get; } = [];

        public Dictionary<string, List<object>> Events { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public ConcurrentQueue<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Calls.Enqueue(uri.PathAndQuery);
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            object body = uri.AbsolutePath switch
            {
                "/discovery/v2/attractions.json" => new { _embedded = new { attractions = Attractions }, page = new { totalPages = 1 } },
                "/discovery/v2/events.json" => new
                {
                    _embedded = new { events = Events.GetValueOrDefault(query["attractionId"] ?? "") ?? [] },
                    page = new { totalPages = 1 },
                },
                _ => new { },
            };
            var json = JsonSerializer.Serialize(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
