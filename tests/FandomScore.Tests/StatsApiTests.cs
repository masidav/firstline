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
/// GET /me/stats and GET /users/{handle}/stats against a real database: counts skip deleted content, reversed awards,
/// and inactive artists; another fan's stats go to the fan and their followers only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StatsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] CountGroups = ["listening", "journal", "proof", "social"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"stats_api_{Guid.NewGuid():N}");
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
    public async Task A_new_fan_has_zeros_everywhere()
    {
        var stats = await StatsAsync(await SignInAsync("fan_one"));

        Assert.Empty(stats.GetProperty("artists").EnumerateArray());
        Assert.All(CountGroups, group =>
            Assert.All(stats.GetProperty(group).EnumerateObject(), p => Assert.Equal(0, p.Value.GetInt32())));
        Assert.Equal(0m, stats.GetProperty("ledger_points").GetDecimal());
    }

    [Fact]
    public async Task Counts_skip_deleted_content_reversed_awards_unverified_proof_and_inactive_artists()
    {
        var token = await SignInAsync("fan_one");
        var artist = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 2);
        var paused = await ScoringSetup.ActiveArtistAsync(_db, "sp-gone", "Gone", releases: 1);
        var me = await ScoringSetup.FanAsync(_db, "fan_one", artist, liked: 10, playlist: 2, albums: 1, rank: null, recent: 3);
        var other = await ScoringSetup.FanAsync(_db, "fan_two", artist, liked: 100, playlist: 100, albums: 2, rank: 1, recent: 50);
        await ScoringSetup.FanAsync(_db, "fan_one", paused, liked: 40, playlist: 0, albums: 1, rank: null, recent: 0);

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE artists SET status = 'paused' WHERE id = @paused;
                SELECT request_artist_rescore(@artist);

                -- Journal: a reviewed, rated log; a relisten; a log deleted later (its award is reversed).
                INSERT INTO album_logs (user_id, album_id, rating, review)
                  SELECT @me, id, 8, repeat('So good. ', 15) FROM albums WHERE spotify_album_id = 'sp-geese-al-1';
                INSERT INTO album_logs (user_id, album_id, is_relisten)
                  SELECT @me, id, true FROM albums WHERE spotify_album_id = 'sp-geese-al-1';
                INSERT INTO album_logs (user_id, album_id)
                  SELECT @me, id FROM albums WHERE spotify_album_id = 'sp-geese-al-2';
                UPDATE album_logs SET deleted_at = now()
                 WHERE user_id = @me AND album_id = (SELECT id FROM albums WHERE spotify_album_id = 'sp-geese-al-2');

                -- Comments on another fan's log: one kept, one deleted.
                INSERT INTO album_logs (user_id, album_id)
                  SELECT @other, id FROM albums WHERE spotify_album_id = 'sp-geese-al-2';
                INSERT INTO comments (album_log_id, user_id, body)
                  SELECT id, @me, 'Great pick, this one grew on me.' FROM album_logs WHERE user_id = @other;
                INSERT INTO comments (album_log_id, user_id, body, deleted_at)
                  SELECT id, @me, 'Changed my mind about this one.', now() FROM album_logs WHERE user_id = @other;
                -- A comment on a log the other fan deletes afterwards: gone with the log.
                INSERT INTO album_logs (user_id, album_id, deleted_at)
                  SELECT @other, id, now() FROM albums WHERE spotify_album_id = 'sp-geese-al-1';
                INSERT INTO comments (album_log_id, user_id, body)
                  SELECT id, @me, 'Still thinking about this.' FROM album_logs WHERE user_id = @other AND deleted_at IS NOT NULL;

                -- The other fan follows me.
                INSERT INTO follows (follower_id, followee_id) VALUES (@other, @me);

                INSERT INTO lists (user_id, title) VALUES (@me, 'Desert island');

                -- Proof: a verified ticket counts; pending merch doesn't.
                INSERT INTO submissions (user_id, artist_id, kind) VALUES (@me, @artist, 'ticket'), (@me, @artist, 'merch');
                UPDATE submissions SET status = 'verified', verified_at = now() WHERE user_id = @me AND kind = 'ticket';
                """,
                new { me, other, artist, paused });
        }

        // Pausing queued a run for that artist too; run them all.
        while (await ScoringSetup.RunQueuedAsync(_db))
        {
        }

        var stats = await StatsAsync(token);

        var geese = Assert.Single(stats.GetProperty("artists").EnumerateArray());
        Assert.Equal("Geese", geese.GetProperty("artist").GetProperty("name").GetString());
        // My journal and proof points outweigh the other fan's Spotify signals (each of the 18 Spotify signals is
        // worth at most 100/18 points; how much ledger points should weigh is an open decision), so I rank first of two.
        Assert.Equal(1, geese.GetProperty("rank").GetInt32());
        Assert.Equal(2, geese.GetProperty("fans").GetInt32());
        Assert.True(geese.GetProperty("is_estimated").GetBoolean());

        var listening = stats.GetProperty("listening");
        Assert.Equal((1, 10, 2, 1, 3), (listening.GetProperty("artists").GetInt32(), listening.GetProperty("liked_tracks").GetInt32(),
            listening.GetProperty("playlist_tracks").GetInt32(), listening.GetProperty("distinct_albums").GetInt32(),
            listening.GetProperty("recent_plays").GetInt32()));
        Assert.Equal((0, 0, 0, 0), (listening.GetProperty("saved_albums").GetInt32(), listening.GetProperty("top_tracks").GetInt32(),
            listening.GetProperty("followed_artists").GetInt32(), listening.GetProperty("plays_last_quarter").GetInt32()));

        var journal = stats.GetProperty("journal");
        Assert.Equal((2, 1, 1, 1, 1, 1, 1), (journal.GetProperty("logs").GetInt32(), journal.GetProperty("albums_logged").GetInt32(),
            journal.GetProperty("relistens").GetInt32(), journal.GetProperty("reviews").GetInt32(),
            journal.GetProperty("ratings").GetInt32(), journal.GetProperty("lists").GetInt32(),
            journal.GetProperty("comments").GetInt32()));

        var proof = stats.GetProperty("proof");
        Assert.Equal((1, 0, 0), (proof.GetProperty("tickets").GetInt32(), proof.GetProperty("merch").GetInt32(),
            proof.GetProperty("screenshots").GetInt32()));

        var social = stats.GetProperty("social");
        Assert.Equal((1, 0), (social.GetProperty("followers").GetInt32(), social.GetProperty("following").GetInt32()));

        // Exactly the unreversed awards in the ledger; the deleted log's and deleted comment's awards are reversed.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            var expected = await connection.ExecuteScalarAsync<decimal>(
                "SELECT COALESCE(sum(points), 0) FROM score_events WHERE user_id = @me AND reversed_at IS NULL", new { me });
            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM score_events WHERE user_id = @me AND reversed_at IS NOT NULL)", new { me }));
            Assert.True(expected > 0);
            Assert.Equal(expected, stats.GetProperty("ledger_points").GetDecimal());
        }
    }

    [Fact]
    public async Task Stats_need_sign_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/v1/me/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/v1/users/fan_one/stats")).StatusCode);
    }

    // Stats are followers-only, none public by default (decided 2026-10-25): the fan and their followers read them,
    // anyone else gets 403 followers_only, the same as the grab bag. A removed follower is anyone else again.
    [Fact]
    public async Task Another_fans_stats_go_to_the_fan_and_their_followers_only()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        var artist = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                  SELECT id, @artist, 70, 92, false, 0.1 FROM users WHERE handle = 'ana';
                INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks) SELECT id, @artist, 40 FROM users WHERE handle = 'ana';
                -- Logs for everyone, for followers, and for Ana alone; a public list and a private one.
                INSERT INTO album_logs (user_id, album_id, visibility)
                  SELECT u.id, al.id, v.visibility::visibility
                  FROM users u, albums al, (VALUES ('public'), ('followers'), ('private')) v (visibility)
                  WHERE u.handle = 'ana' AND al.artist_id = @artist;
                INSERT INTO lists (user_id, title, visibility)
                  SELECT id, t.title, t.visibility::visibility FROM users, (VALUES ('Desert island', 'public'), ('Secret', 'private')) t (title, visibility)
                  WHERE handle = 'ana';
                """,
                new { artist });
        }

        // Ben follows Ana; Ana follows Cat; Cat follows nobody.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/users/cat/follow", ana)).StatusCode);

        // A stranger: 403 followers_only, nothing about the numbers leaks.
        var refused = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", cat), HttpStatusCode.Forbidden);
        Assert.Equal("followers_only", refused.GetProperty("code").GetString());

        // A follower: fandom stats without listening counts; private logs and lists aren't counted.
        var seen = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben), HttpStatusCode.OK);
        var geese = Assert.Single(seen.GetProperty("artists").EnumerateArray());
        Assert.Equal((92m, 1, false), (geese.GetProperty("percentile").GetDecimal(), geese.GetProperty("rank").GetInt32(),
            geese.GetProperty("is_estimated").GetBoolean()));
        Assert.False(seen.TryGetProperty("listening", out _));
        Assert.Equal((2, 1), (seen.GetProperty("journal").GetProperty("logs").GetInt32(), seen.GetProperty("journal").GetProperty("lists").GetInt32()));
        Assert.Equal((1, 1), (seen.GetProperty("social").GetProperty("followers").GetInt32(), seen.GetProperty("social").GetProperty("following").GetInt32()));

        // The fan through the same route: full journal counts, still no listening block (that's GET /me/stats).
        var own = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/@ana/stats", ana), HttpStatusCode.OK);
        Assert.Equal((3, 2), (own.GetProperty("journal").GetProperty("logs").GetInt32(), own.GetProperty("journal").GetProperty("lists").GetInt32()));
        Assert.False(own.TryGetProperty("listening", out _));
        var mine = await StatsAsync(ana);
        Assert.Equal(40, mine.GetProperty("listening").GetProperty("liked_tracks").GetInt32());
        Assert.Equal((1, 1), (mine.GetProperty("social").GetProperty("followers").GetInt32(), mine.GetProperty("social").GetProperty("following").GetInt32()));

        // Ana removes Ben as a follower: he's a stranger again, and her follower count drops.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/followers/ben", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben)).StatusCode);
        Assert.Equal(0, (await StatsAsync(ana)).GetProperty("social").GetProperty("followers").GetInt32());

        // Unknown and deleted accounts: 404 for everyone, follower or not.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/users/nobody/stats", ben)).StatusCode);
        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE handle = 'ana'");
        }

        var gone = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben), HttpStatusCode.NotFound);
        Assert.Equal("not_found", gone.GetProperty("code").GetString());
        // Deleting the account scrubbed her follows, so Cat lost a follower.
        Assert.Equal(0, (await StatsAsync(cat)).GetProperty("social").GetProperty("followers").GetInt32());
    }

    // Through the API rather than SQL: deleting a log drops it from the counts and reverses its award in the ledger
    // (reverse_points; score_events is append-only), for the fan and for a follower alike.
    [Fact]
    public async Task Deleting_a_log_drops_the_count_and_reverses_its_points()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var artist = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        Guid album;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            album = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM albums WHERE artist_id = @artist", new { artist });
        }

        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/logs", ana,
            new { album_id = album, rating = 9, review = string.Concat(Enumerable.Repeat("So good. ", 15)) }), HttpStatusCode.Created);
        var logId = created.GetProperty("log").GetProperty("id").GetGuid();
        Assert.NotEmpty(created.GetProperty("points_awarded").EnumerateArray());

        var before = await StatsAsync(ana);
        Assert.Equal((1, 1, 1), (before.GetProperty("journal").GetProperty("logs").GetInt32(),
            before.GetProperty("journal").GetProperty("reviews").GetInt32(), before.GetProperty("journal").GetProperty("ratings").GetInt32()));
        Assert.True(before.GetProperty("ledger_points").GetDecimal() > 0);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/logs/{logId}", ana)).StatusCode);

        var after = await StatsAsync(ana);
        Assert.Equal((0, 0, 0, 0), (after.GetProperty("journal").GetProperty("logs").GetInt32(), after.GetProperty("journal").GetProperty("albums_logged").GetInt32(),
            after.GetProperty("journal").GetProperty("reviews").GetInt32(), after.GetProperty("journal").GetProperty("ratings").GetInt32()));
        Assert.Equal(0m, after.GetProperty("ledger_points").GetDecimal());
        var followerView = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben), HttpStatusCode.OK);
        Assert.Equal((0, 0m), (followerView.GetProperty("journal").GetProperty("logs").GetInt32(), followerView.GetProperty("ledger_points").GetDecimal()));

        // The ledger kept every row: the awards are reversed, not removed.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            var (total, reversed) = await connection.QuerySingleAsync<(int Total, int Reversed)>(
                """
                SELECT count(*)::int, count(*) FILTER (WHERE reversed_at IS NOT NULL)::int
                FROM score_events WHERE user_id = (SELECT id FROM users WHERE handle = 'ana')
                """);
            Assert.True(total > 0);
            Assert.Equal(total, reversed);
        }
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<JsonElement> StatsAsync(string token) =>
        await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/stats", token), HttpStatusCode.OK);

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
