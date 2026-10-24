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

/// <summary>GET /me/stats against a real database: counts skip deleted content, reversed awards, and inactive artists.</summary>
[Collection(PostgresCollection.Name)]
public sealed class StatsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] CountGroups = ["listening", "journal", "proof"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"stats_api_{Guid.NewGuid():N}");
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
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<JsonElement> StatsAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/stats");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }
}
