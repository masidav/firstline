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
/// The mixed home feed (GET /feed): what each fan sees from follows, shows, and their own registrations, and that
/// one seed pages consistently.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"feed-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"feed_api_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 2);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_follower_gets_the_whole_mix_and_a_stranger_only_the_public_parts()
    {
        var ana = await FanAsync("ana", score: 80, "us-il-chicago");      // posts things
        var ben = await FanAsync("ben", score: 70, "us-il-chicago");      // follows ana, registers for the show
        var cat = await FanAsync("cat", score: null);                     // follows nobody, no hub city
        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);
        Guid eventId, allocationId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            var anaId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE handle = 'ana'");
            eventId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO events (artist_id, name, venue, city_id, starts_at) VALUES (@_artistId, 'Geese at Metro', 'Metro', 'us-il-chicago', now() + interval '20 days') RETURNING id",
                new { _artistId });
            allocationId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
                VALUES (@eventId, 100, 4500, 10, now() - interval '1 hour', now() + interval '5 days', now()) RETURNING id
                """,
                new { eventId });
            await connection.ExecuteAsync(
                """
                -- A public log, a followers-only log, and a private one (never shown).
                INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
                  SELECT @anaId, id, 9, 'Public: loud and great.', 'public' FROM albums WHERE spotify_album_id = 'sp-geese-al-1';
                INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
                  SELECT @anaId, id, 7, 'Followers: a grower.', 'followers' FROM albums WHERE spotify_album_id = 'sp-geese-al-2';
                INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
                  SELECT @anaId, id, 2, 'Private: never mind.', 'private' FROM albums WHERE spotify_album_id = 'sp-geese-al-2';
                -- A shared merch photo and a verified concert photo (not shared) for the proof stat.
                INSERT INTO submissions (user_id, artist_id, kind, caption) VALUES (@anaId, @_artistId, 'merch', 'New shirt');
                INSERT INTO submissions (user_id, artist_id, kind, event_id) VALUES (@anaId, @_artistId, 'concert_photo', @eventId);
                UPDATE submissions SET status = 'verified', verified_at = now() WHERE user_id = @anaId;
                UPDATE submissions SET shared_at = now() WHERE user_id = @anaId AND kind = 'merch';
                -- Two grab-bag songs.
                INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id, album_title)
                  VALUES (@anaId, 1, '1aaaaaaaaaaaaaaaaaaaaa', 'Cowboy Nudes', @_artistId, '3D Country'),
                         (@anaId, 2, '2aaaaaaaaaaaaaaaaaaaaa', '2122', @_artistId, '3D Country');
                """,
                new { anaId, _artistId, eventId });
        }

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", ben), HttpStatusCode.Created);

        var benFeed = await AllItemsAsync(ben, "seed-a");
        var types = benFeed.GroupBy(i => i.GetProperty("type").GetString()!).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Single(types["photo"]);
        Assert.Equal(2, types["album_log"].Count);
        Assert.Contains(types["album_log"], l => l.GetProperty("log").GetProperty("visibility").GetString() == "followers");
        Assert.DoesNotContain(types["album_log"], l => l.GetProperty("log").GetProperty("review").GetString()!.StartsWith("Private", StringComparison.Ordinal));
        Assert.Equal(["2122", "Cowboy Nudes"], types["grab_bag"].Select(g => g.GetProperty("grab_bag").GetProperty("title").GetString()).Order());
        Assert.All(types["grab_bag"], g => Assert.True(g.GetProperty("following").GetBoolean()));
        Assert.All(types["grab_bag"], g => Assert.Equal("ana", g.GetProperty("user").GetProperty("handle").GetString()));

        var concert = Assert.Single(types["concert"]);
        Assert.Equal(JsonValueKind.Null, concert.GetProperty("user").ValueKind);
        Assert.Equal(("Geese at Metro", "open", true), (concert.GetProperty("concert").GetProperty("event").GetProperty("name").GetString(),
            concert.GetProperty("concert").GetProperty("allocation").GetProperty("status").GetString(),
            concert.GetProperty("concert").GetProperty("registered").GetBoolean()));

        var registration = Assert.Single(types["registration"]);
        Assert.Equal(("ben", "registered", 1), (registration.GetProperty("user").GetProperty("handle").GetString(),
            registration.GetProperty("registration").GetProperty("registration").GetProperty("status").GetString(),
            registration.GetProperty("registration").GetProperty("standing").GetProperty("rank").GetInt32()));

        var stats = types["stat"].Select(s => s.GetProperty("stat")).ToDictionary(s => s.GetProperty("kind").GetString()!);
        Assert.Equal(["concert_photos", "merch", "points", "top_fan"], stats.Keys.Order());
        Assert.Equal((80m, 1, 2), (stats["top_fan"].GetProperty("percentile").GetDecimal(), stats["top_fan"].GetProperty("rank").GetInt32(),
            stats["top_fan"].GetProperty("fans").GetInt32()));
        Assert.Equal(1m, stats["merch"].GetProperty("value").GetDecimal());
        Assert.Equal("Geese", types["stat"].Single(s => s.GetProperty("stat").GetProperty("kind").GetString() == "top_fan")
            .GetProperty("artist").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, types["stat"].Single(s => s.GetProperty("stat").GetProperty("kind").GetString() == "points")
            .GetProperty("artist").ValueKind);

        // Cat follows nobody and has no score or hub: only everyone's public posts.
        var catTypes = (await AllItemsAsync(cat, "seed-a")).Select(i => i.GetProperty("type").GetString()).ToList();
        Assert.Equal(["album_log", "photo"], catTypes.Order());

        // Ana sees her own things (as "following: false") but no stats about herself, and the show (her artist).
        var anaTypes = (await AllItemsAsync(ana, "seed-a")).GroupBy(i => i.GetProperty("type").GetString()!).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, anaTypes["concert"]);
        Assert.Equal(2, anaTypes["album_log"]);
        Assert.False(anaTypes.ContainsKey("stat"));
        Assert.False(anaTypes.ContainsKey("grab_bag"));
    }

    [Fact]
    public async Task One_seed_pages_consistently_and_another_seed_reshuffles()
    {
        var ana = await FanAsync("ana", score: 80, "us-il-chicago");
        var ben = await FanAsync("ben", score: null);
        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO album_logs (user_id, album_id, rating, visibility)
                  SELECT u.id, al.id, n, 'public' FROM users u, albums al, generate_series(1, 10) n
                   WHERE u.handle = 'ana' AND al.spotify_album_id = 'sp-geese-al-1';
                INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
                  SELECT u.id, n, lpad(n::text, 22, 'a'), 'Song ' || n, @_artistId FROM users u, generate_series(1, 5) n WHERE u.handle = 'ana';
                """,
                new { _artistId });
        }

        var whole = (await AllItemsAsync(ben, "mix-1", limit: 100)).Select(Key).ToList();
        var paged = (await AllItemsAsync(ben, "mix-1", limit: 4)).Select(Key).ToList();
        Assert.Equal(whole, paged);
        Assert.Equal(whole.Count, whole.Distinct().Count());
        Assert.True(whole.Count >= 16, $"expected logs, songs, and a stat, got {whole.Count}");

        var other = (await AllItemsAsync(ben, "mix-2", limit: 100)).Select(Key).ToList();
        Assert.Equal(whole.Order(), other.Order());
        Assert.NotEqual(whole, other);

        var first = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=3", ben), HttpStatusCode.OK);
        Assert.Matches("^[0-9]{8}$", first.GetProperty("seed").GetString());
        var bad = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?seed=no%20spaces", ben), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("seed", bad.GetProperty("errors")[0].GetProperty("field").GetString());

        static string Key(JsonElement item) => $"{item.GetProperty("type").GetString()}:{item.GetProperty("id").GetString()}";
    }

    private async Task<List<JsonElement>> AllItemsAsync(string token, string seed, int limit = 50)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var query = $"/v1/feed?limit={limit}&seed={seed}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await JsonAsync(await SendAsync(HttpMethod.Get, query, token), HttpStatusCode.OK);
            Assert.Equal(seed, page.GetProperty("seed").GetString());
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);
        return items;
    }

    /// <summary>A signed-in fan with hub cities and, unless null, a score for the artist.</summary>
    private async Task<string> FanAsync(string handle, decimal? score, params string[] hubs)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
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
