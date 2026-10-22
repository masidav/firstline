using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Spotify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Albums and the album journal (openapi tags Albums and Journal) against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class JournalApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string Radiohead = "4Z8W4fKeB5YxbusRsdQVPb";
    private const string LongReview = "In Rainbows is the sound of a band relaxing into its own strangeness; every track feels lived-in and generous.";

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _inRainbows;
    private Guid _theBends;
    private Guid _lostEp;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"journal_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        await using (var connection = await _db.OpenConnectionAsync())
        {
            // Separate statements: a data-modifying CTE can't see rows another CTE of the same statement inserted.
            _artistId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO artists (name, spotify_artist_id, image_url, catalog_loaded_at) VALUES ('Radiohead', @Radiohead, 'https://i.scdn.co/rh', now()) RETURNING id",
                new { Radiohead });
            await connection.ExecuteAsync(
                """
                INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES (@_artistId, '{}', '[]');
                UPDATE artists SET status = 'active' WHERE id = @_artistId;
                """,
                new { _artistId });
            _inRainbows = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO albums (artist_id, title, release_date, release_date_precision, spotify_album_id, cover_url, album_type)
                VALUES (@_artistId, 'In Rainbows', '2007-10-10', 'day', 'al-rainbows', 'https://i.scdn.co/al-rainbows', 'album') RETURNING id
                """,
                new { _artistId });
            _theBends = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO albums (artist_id, title, release_date, release_date_precision, spotify_album_id, cover_url, album_type)
                VALUES (@_artistId, 'The Bends', '1995-01-01', 'year', 'al-bends', 'https://i.scdn.co/al-bends', 'album') RETURNING id
                """,
                new { _artistId });
            _lostEp = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO albums (artist_id, title, release_date, spotify_album_id, cover_url, album_type, delisted_at)
                VALUES (@_artistId, 'Lost EP', '2001-05-05', 'al-lost', 'https://i.scdn.co/al-lost', 'single', now()) RETURNING id
                """,
                new { _artistId });
            await connection.ExecuteAsync(
                """
                INSERT INTO tracks (album_id, title, track_number, disc_number, spotify_track_id)
                VALUES (@_inRainbows, '15 Step', 1, 1, 'tr-15'), (@_inRainbows, 'Bodysnatchers', 2, 1, 'tr-body')
                """,
                new { _inRainbows });
        }

        _spotify.Artists.Add(new FakeSpotify.Artist(Radiohead, "Radiohead"));
        _spotify.Artists.Add(new FakeSpotify.Artist("0aaaaaaaaaaaaaaaaaaaaa", "Geese", "indie rock"));

        var options = FakeSpotify.Options();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Spotify:ClientId", options.ClientId);
            builder.UseSetting("Spotify:ClientSecret", "test-client-secret");
            builder.UseSetting("Spotify:RedirectUris", options.RedirectUris);
            builder.UseSetting("Spotify:TokenKey", options.TokenKey);
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddHttpClient<SpotifyClient>().ConfigurePrimaryHttpMessageHandler(() => _spotify);
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Artist_albums_list_live_releases_newest_first_and_detail_has_tracks()
    {
        var token = await SignInAsync("fan_one");

        var page = await JsonAsync(await GetAsync($"/v1/artists/{_artistId}/albums", token), HttpStatusCode.OK);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["In Rainbows", "The Bends"], items.Select(a => a.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_cursor").ValueKind);
        var bends = items[1];
        Assert.Equal(("1995-01-01", "year", "album", "al-bends", false, 0),
            (bends.GetProperty("release_date").GetString(), bends.GetProperty("release_date_precision").GetString(),
             bends.GetProperty("album_type").GetString(), bends.GetProperty("spotify_album_id").GetString(),
             bends.GetProperty("delisted").GetBoolean(), bends.GetProperty("community").GetProperty("log_count").GetInt32()));
        Assert.Equal("Radiohead", bends.GetProperty("artist").GetProperty("name").GetString());

        var detail = await JsonAsync(await GetAsync($"/v1/albums/{_inRainbows}", token), HttpStatusCode.OK);
        Assert.Equal(["15 Step", "Bodysnatchers"], detail.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        Assert.Equal("tr-15", detail.GetProperty("tracks")[0].GetProperty("spotify_track_id").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("my_latest_log").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("community").GetProperty("average_rating").ValueKind);

        // A delisted album is not listed but still readable, without cover art or Spotify link.
        var lost = await JsonAsync(await GetAsync($"/v1/albums/{_lostEp}", token), HttpStatusCode.OK);
        Assert.True(lost.GetProperty("delisted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, lost.GetProperty("cover_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, lost.GetProperty("spotify_album_id").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/v1/albums/{Guid.NewGuid()}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/v1/artists/{Guid.NewGuid()}/albums", token)).StatusCode);
    }

    [Fact]
    public async Task Album_search_matches_titles_and_artist_names_and_suggests_untracked_artists_only_when_nothing_matches()
    {
        var token = await SignInAsync("fan_one");

        var byTitle = await JsonAsync(await GetAsync("/v1/albums/search?q=bend", token), HttpStatusCode.OK);
        Assert.Equal(["The Bends"], byTitle.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        Assert.Empty(byTitle.GetProperty("untracked_artists").EnumerateArray());

        var byArtist = await JsonAsync(await GetAsync("/v1/albums/search?q=radio", token), HttpStatusCode.OK);
        Assert.Equal(2, byArtist.GetProperty("items").GetArrayLength());   // the delisted EP is left out
        Assert.DoesNotContain(_spotify.Calls, c => c.Path == "search");

        var nothing = await JsonAsync(await GetAsync("/v1/albums/search?q=geese", token), HttpStatusCode.OK);
        Assert.Empty(nothing.GetProperty("items").EnumerateArray());
        var suggestion = Assert.Single(nothing.GetProperty("untracked_artists").EnumerateArray());
        Assert.Equal(("Geese", false), (suggestion.GetProperty("name").GetString(), suggestion.GetProperty("tracked").GetBoolean()));

        // Tracked artists are never suggested, and a Spotify outage just means no suggestions.
        _spotify.ApiStatus = HttpStatusCode.Forbidden;
        var outage = await JsonAsync(await GetAsync("/v1/albums/search?q=zzzz", token), HttpStatusCode.OK);
        Assert.Empty(outage.GetProperty("untracked_artists").EnumerateArray());

        var problem = await JsonAsync(await GetAsync("/v1/albums/search?q=b", token), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("q", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Logging_an_album_earns_points_and_a_second_log_is_a_relisten()
    {
        var token = await SignInAsync("fan_one");

        var first = await JsonAsync(await PostAsync("/v1/logs", token,
            new { album_id = _inRainbows, rating = 9, review = LongReview, listened_on = "2026-10-20" }), HttpStatusCode.Created);
        var log = first.GetProperty("log");
        Assert.Equal((9, false, "public", "2026-10-20", "fan_one", "In Rainbows"),
            (log.GetProperty("rating").GetInt32(), log.GetProperty("is_relisten").GetBoolean(), log.GetProperty("visibility").GetString(),
             log.GetProperty("listened_on").GetString(), log.GetProperty("user").GetProperty("handle").GetString(),
             log.GetProperty("album").GetProperty("title").GetString()));
        Assert.Equal([("album_logged", 3.0), ("review_written", 4.0)],
            first.GetProperty("points_awarded").EnumerateArray().Select(p => (p.GetProperty("rule_key").GetString(), p.GetProperty("points").GetDouble())));

        var second = await JsonAsync(await PostAsync("/v1/logs", token, new { album_id = _inRainbows, rating = 8 }), HttpStatusCode.Created);
        Assert.True(second.GetProperty("log").GetProperty("is_relisten").GetBoolean());
        Assert.Equal(["album_relisten"], second.GetProperty("points_awarded").EnumerateArray().Select(p => p.GetProperty("rule_key").GetString()));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            second.GetProperty("log").GetProperty("listened_on").GetString());

        var detail = await JsonAsync(await GetAsync($"/v1/albums/{_inRainbows}", token), HttpStatusCode.OK);
        Assert.Equal((2, 4.25), (detail.GetProperty("community").GetProperty("log_count").GetInt32(),
            detail.GetProperty("community").GetProperty("average_rating").GetDouble()));
        Assert.Equal(second.GetProperty("log").GetProperty("id").GetString(), detail.GetProperty("my_latest_log").GetProperty("id").GetString());

        var diary = await JsonAsync(await GetAsync($"/v1/me/logs?artist_id={_artistId}&year=2026", token), HttpStatusCode.OK);
        Assert.Equal(2, diary.GetProperty("items").GetArrayLength());
        var paged = await JsonAsync(await GetAsync("/v1/me/logs?limit=1", token), HttpStatusCode.OK);
        var next = paged.GetProperty("next_cursor").GetString()!;
        var rest = await JsonAsync(await GetAsync($"/v1/me/logs?limit=1&cursor={Uri.EscapeDataString(next)}", token), HttpStatusCode.OK);
        Assert.Single(rest.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("next_cursor").ValueKind);
        Assert.Empty((await JsonAsync(await GetAsync("/v1/me/logs?year=2019", token), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"album_id":"ALBUM","rating":11}""", "rating")]
    [InlineData("""{"album_id":"ALBUM","listened_on":"2099-01-01"}""", "listened_on")]
    [InlineData("""{"album_id":"ALBUM","visibility":"friends"}""", "visibility")]
    [InlineData("""{"rating":5}""", "album_id")]
    public async Task Bad_log_fields_are_rejected(string body, string field)
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/logs", token,
            new StringContent(body.Replace("ALBUM", _inRainbows.ToString(), StringComparison.Ordinal), new MediaTypeHeaderValue("application/json"))),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Unknown_or_delisted_albums_cannot_be_logged()
    {
        var token = await SignInAsync("fan_one");

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/v1/logs", token, new { album_id = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/v1/logs", token, new { album_id = _lostEp })).StatusCode);
    }

    [Fact]
    public async Task An_idempotency_key_replays_the_first_response_and_rejects_a_different_body()
    {
        var token = await SignInAsync("fan_one");
        var body = new { album_id = _theBends, rating = 7 };

        var first = await JsonAsync(await PostAsync("/v1/logs", token, body, idempotencyKey: "key-1"), HttpStatusCode.Created);
        var replay = await JsonAsync(await PostAsync("/v1/logs", token, body, idempotencyKey: "key-1"), HttpStatusCode.Created);
        Assert.Equal(first.GetProperty("log").GetProperty("id").GetString(), replay.GetProperty("log").GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("points_awarded").GetRawText(), replay.GetProperty("points_awarded").GetRawText());

        var conflict = await JsonAsync(await PostAsync("/v1/logs", token, new { album_id = _theBends, rating = 8 }, idempotencyKey: "key-1"),
            HttpStatusCode.Conflict);
        Assert.Equal("idempotency_conflict", conflict.GetProperty("code").GetString());

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM album_logs"));

        // Another fan's key space is separate.
        var other = await SignInAsync("fan_two");
        await JsonAsync(await PostAsync("/v1/logs", other, body, idempotencyKey: "key-1"), HttpStatusCode.Created);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM album_logs"));
    }

    [Fact]
    public async Task Private_logs_are_invisible_to_others_and_only_owners_can_edit()
    {
        var owner = await SignInAsync("fan_one");
        var other = await SignInAsync("fan_two");
        var privateId = (await JsonAsync(await PostAsync("/v1/logs", owner, new { album_id = _inRainbows, visibility = "private" }), HttpStatusCode.Created))
            .GetProperty("log").GetProperty("id").GetString();
        var publicId = (await JsonAsync(await PostAsync("/v1/logs", owner, new { album_id = _theBends, review = "Fine." }), HttpStatusCode.Created))
            .GetProperty("log").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"/v1/logs/{privateId}", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/v1/logs/{privateId}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"/v1/logs/{publicId}", other)).StatusCode);

        var community = await JsonAsync(await GetAsync($"/v1/albums/{_inRainbows}/logs", other), HttpStatusCode.OK);
        Assert.Empty(community.GetProperty("items").EnumerateArray());
        var profile = await JsonAsync(await GetAsync("/v1/users/fan_one/logs", other), HttpStatusCode.OK);
        Assert.Equal([publicId], profile.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetString()));
        var withReview = await JsonAsync(await GetAsync($"/v1/albums/{_theBends}/logs?with_review=true&sort=discussed", owner), HttpStatusCode.OK);
        Assert.Single(withReview.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/v1/users/nobody/logs", other)).StatusCode);

        var patch = JsonContent.Create(new { rating = 4 });
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Patch, $"/v1/logs/{publicId}", other, patch)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Patch, $"/v1/logs/{privateId}", other, JsonContent.Create(new { rating = 4 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/v1/logs/{publicId}", other)).StatusCode);
    }

    [Fact]
    public async Task Editing_a_review_awards_once_and_deleting_reverses_everything()
    {
        var token = await SignInAsync("fan_one");
        var logId = (await JsonAsync(await PostAsync("/v1/logs", token, new { album_id = _inRainbows, rating = 6 }), HttpStatusCode.Created))
            .GetProperty("log").GetProperty("id").GetString();

        var longer = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/logs/{logId}", token,
            JsonContent.Create(new { review = LongReview, rating = (int?)null, visibility = "followers" })), HttpStatusCode.OK);
        Assert.Equal(["review_written"], longer.GetProperty("points_awarded").EnumerateArray().Select(p => p.GetProperty("rule_key").GetString()));
        Assert.Equal(JsonValueKind.Null, longer.GetProperty("log").GetProperty("rating").ValueKind);
        Assert.Equal("followers", longer.GetProperty("log").GetProperty("visibility").GetString());

        var shorter = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/logs/{logId}", token, JsonContent.Create(new { review = "Short." })), HttpStatusCode.OK);
        Assert.Empty(shorter.GetProperty("points_awarded").EnumerateArray());
        var again = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/logs/{logId}", token, JsonContent.Create(new { review = LongReview })), HttpStatusCode.OK);
        Assert.Equal(["review_written"], again.GetProperty("points_awarded").EnumerateArray().Select(p => p.GetProperty("rule_key").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/logs/{logId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/v1/logs/{logId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/v1/logs/{logId}", token)).StatusCode);

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM score_events WHERE source_id = @logId::uuid AND reversed_at IS NULL", new { logId }));
        Assert.Equal(0, (await JsonAsync(await GetAsync("/v1/me/logs", token), HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> GetAsync(string path, string token) => SendAsync(HttpMethod.Get, path, token);

    private Task<HttpResponseMessage> PostAsync(string path, string token, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        var errors = string.Join("\n", _logs.Where(l => l.Contains("Exception", StringComparison.Ordinal)).TakeLast(3));
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}\n{errors}");
        return JsonDocument.Parse(body).RootElement;
    }
}
