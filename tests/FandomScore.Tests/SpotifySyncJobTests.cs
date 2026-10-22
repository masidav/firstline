using System.Net;
using System.Text.Json;
using Dapper;
using FandomScore.Database;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.SpotifySync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The Spotify sync and scheduling jobs against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpotifySyncJobTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    // Artist A is tracked and active, B is still onboarding, X is not tracked at all.
    private const string ArtistA = "sp-art-a";
    private const string ArtistB = "sp-art-b";
    private const string ArtistX = "sp-art-x";

    private readonly FakeSpotify _spotify = new();
    private readonly SpotifyTokenProtector _protector = new(FakeSpotify.Options());
    private NpgsqlDataSource _db = null!;
    private Guid _userId;
    private Guid _artistA;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"spotify_sync_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        await using var connection = await _db.OpenConnectionAsync();
        _userId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (handle, email) VALUES ('fan_one', 'fan@example.test') RETURNING id");
        _artistA = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO artists (name, spotify_artist_id) VALUES ('A', @ArtistA);
            INSERT INTO artist_baselines (artist_id, dist_params, curve) SELECT id, '{}', '[]' FROM artists WHERE spotify_artist_id = @ArtistA;
            UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE spotify_artist_id = @ArtistA;
            INSERT INTO artists (name, spotify_artist_id) VALUES ('B', @ArtistB);
            INSERT INTO albums (artist_id, title, spotify_album_id) SELECT id, 'A1', 'al-a1' FROM artists WHERE spotify_artist_id = @ArtistA;
            INSERT INTO tracks (album_id, title, spotify_track_id) SELECT id, 'Track a1', 'tr-a1' FROM albums WHERE spotify_album_id = 'al-a1';
            SELECT id FROM artists WHERE spotify_artist_id = @ArtistA;
            """,
            new { ArtistA, ArtistB });
        await connection.ExecuteAsync(
            """
            INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes)
            VALUES (@_userId, 'sp-user-1', @token, @scopes);
            SELECT request_spotify_sync(@_userId);
            """,
            new { _userId, token = _protector.Protect(_userId, "refresh-0"), scopes = SpotifyOptions.RequiredScopes.ToArray() });
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Sync_stores_signals_for_tracked_active_artists_only()
    {
        _spotify.SavedTracks.AddRange([
            FakeSpotify.Saved(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)),
            FakeSpotify.Saved(FakeSpotify.Track("tr-a2", "al-a2", ArtistA)),
            FakeSpotify.Saved(FakeSpotify.Track("tr-x1", "al-x1", ArtistX)),
            FakeSpotify.Saved(FakeSpotify.Track("tr-b1", "al-b1", ArtistB)),
            FakeSpotify.Saved(FakeSpotify.Track("tr-feat", "al-x2", ArtistX, ArtistA)),
        ]);
        _spotify.Playlists.AddRange([
            FakeSpotify.Playlist("pl-mine", "sp-user-1"),
            FakeSpotify.Playlist("pl-shared", "someone", collaborative: true),
            FakeSpotify.Playlist("pl-followed", "someone"),
        ]);
        _spotify.PlaylistItems["pl-mine"] = [
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a3", "al-a3", ArtistA)),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-x1", "al-x1", ArtistX)),
        ];
        _spotify.PlaylistItems["pl-shared"] = [FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a3", "al-a3", ArtistA))];
        _spotify.TopArtistIds.AddRange([ArtistX, ArtistA]);
        _spotify.RecentlyPlayed.AddRange([
            FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)),
            FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)),
            FakeSpotify.Played(FakeSpotify.Track("tr-x1", "al-x1", ArtistX)),
        ]);

        await RunAsync();

        var signals = Assert.Single(await SignalsAsync());
        Assert.Equal(_artistA, signals.ArtistId);
        Assert.Equal(2, signals.LikedTracks);       // tr-a1, tr-a2; A is only featured on tr-feat
        Assert.Equal(2, signals.PlaylistTracks);    // tr-a1, tr-a3 once each
        Assert.Equal(3, signals.DistinctAlbums);    // al-a1, al-a2, al-a3
        Assert.Equal((short)2, signals.TopArtistRank);
        Assert.Equal(2, signals.RecentPlays);

        // Only catalog tracks are stored, so nothing about X or B.
        Assert.Equal(["tr-a1:liked", "tr-a1:playlist"], await TrackLikesAsync());
        var state = await StateAsync();
        Assert.Equal(("idle", (string?)null, 1, true), (state.SyncState, state.SyncError, state.TrackedArtistsFound, state.Synced));

        // The fan's artists are queued for scoring (percentiles depend on every fan).
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal([_artistA.ToString()], await connection.QueryAsync<string>(
                "SELECT payload->>'artist_id' FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued'"));
        }

        Assert.DoesNotContain(_spotify.Calls, c => c.Path.Contains("pl-followed", StringComparison.Ordinal));
        var refresh = Assert.Single(_spotify.Calls, c => c.Path == "token").Form;
        Assert.Equal(("refresh_token", "refresh-0", FakeSpotify.ClientId),
            (refresh["grant_type"], refresh["refresh_token"], refresh["client_id"]));
        Assert.All(_spotify.Calls.Where(c => c.Path != "token"), c => Assert.Equal("access-1", c.Bearer));
    }

    [Fact]
    public async Task Pages_through_saved_tracks_and_replaces_earlier_results()
    {
        for (var i = 0; i < 120; i++)
        {
            _spotify.SavedTracks.Add(FakeSpotify.Saved(FakeSpotify.Track($"tr-{i}", "al-a1", ArtistA)));
        }

        await RunAsync();
        Assert.Equal(120, Assert.Single(await SignalsAsync()).LikedTracks);
        Assert.Equal(3, _spotify.Calls.Count(c => c.Path == "me/tracks"));

        // Running again gives the current state, not a sum.
        _spotify.SavedTracks.RemoveRange(0, 100);
        await RunAsync();
        Assert.Equal(20, Assert.Single(await SignalsAsync()).LikedTracks);

        _spotify.SavedTracks.Clear();
        await RunAsync();
        Assert.Empty(await SignalsAsync());
        Assert.Equal(0, (await StateAsync()).TrackedArtistsFound);
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_stored()
    {
        _spotify.TokenResponse = FakeSpotify.Tokens("refresh-2");
        await RunAsync();
        Assert.Equal("refresh-2", await StoredTokenAsync());

        // No new token: the old one stays.
        _spotify.TokenResponse = FakeSpotify.Tokens(null);
        await RunAsync();
        Assert.Equal("refresh-2", await StoredTokenAsync());
    }

    [Fact]
    public async Task A_rejected_refresh_token_needs_reauthorization_and_is_not_retried()
    {
        _spotify.TokenStatus = HttpStatusCode.BadRequest;
        _spotify.TokenResponse = new { error = "invalid_grant", error_description = "Refresh token revoked" };

        await RunAsync();

        var state = await StateAsync();
        Assert.Equal(("reauth_required", "spotify_reauth_required"), (state.SyncState, state.SyncError));

        // Later runs leave it alone until the user reconnects.
        _spotify.TokenStatus = HttpStatusCode.OK;
        await RunAsync();
        Assert.Equal(1, _spotify.Calls.Count(c => c.Path == "token"));
    }

    [Fact]
    public async Task Short_rate_limits_are_waited_out()
    {
        _spotify.Interruptions.Enqueue(("me/tracks", () => FakeSpotify.Status(HttpStatusCode.TooManyRequests, TimeSpan.Zero)));
        _spotify.SavedTracks.Add(FakeSpotify.Saved(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)));

        await RunAsync();

        Assert.Equal(1, Assert.Single(await SignalsAsync()).LikedTracks);
        Assert.Equal(2, _spotify.Calls.Count(c => c.Path == "me/tracks"));
    }

    [Fact]
    public async Task Long_rate_limits_and_outages_go_back_to_the_queue_then_fail()
    {
        _spotify.Interruptions.Enqueue(("me/tracks", () => FakeSpotify.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5))));

        var error = await Assert.ThrowsAsync<SpotifyUnavailableException>(() => RunAsync(attempt: 1));
        Assert.Equal(TimeSpan.FromMinutes(5), error.RetryAfter);
        var state = await StateAsync();
        Assert.Equal(("queued", "spotify_unavailable"), (state.SyncState, state.SyncError));

        _spotify.TokenStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<SpotifyUnavailableException>(() => RunAsync(attempt: 10));
        Assert.Equal("failed", (await StateAsync()).SyncState);
    }

    [Fact]
    public async Task Accounts_spotify_refuses_are_marked_failed_without_retrying()
    {
        _spotify.ApiStatus = HttpStatusCode.Forbidden;

        await RunAsync();

        var state = await StateAsync();
        Assert.Equal(("failed", "spotify_forbidden"), (state.SyncState, state.SyncError));
    }

    [Fact]
    public async Task Disconnecting_during_a_sync_leaves_nothing_behind()
    {
        _spotify.SavedTracks.Add(FakeSpotify.Saved(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)));
        _spotify.OnRequest = async path =>
        {
            if (path == "me/playlists")
            {
                await using var connection = await _db.OpenConnectionAsync();
                await connection.ExecuteAsync("DELETE FROM spotify_connections WHERE user_id = @_userId", new { _userId });
            }
        };

        await RunAsync();

        Assert.Empty(await SignalsAsync());
        Assert.Empty(await TrackLikesAsync());
    }

    [Fact]
    public async Task Schedule_queues_connections_not_synced_recently()
    {
        await using var connection = await _db.OpenConnectionAsync();
        var others = await connection.QueryAsync<Guid>(
            """
            INSERT INTO users (handle, email) VALUES ('fan_two', 'two@example.test'), ('fan_three', 'three@example.test'),
                                                     ('fan_four', 'four@example.test')
            RETURNING id
            """);
        var ids = others.ToArray();
        var (recent, stale, reauth) = (ids[0], ids[1], ids[2]);
        await connection.ExecuteAsync(
            """
            UPDATE spotify_connections SET sync_state = 'idle', last_sync_attempt_at = now() - interval '2 days';
            DELETE FROM jobs;
            INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes, sync_state, last_sync_attempt_at) VALUES
              (@recent, 'sp-recent', '\x00', '{}', 'idle', now() - interval '1 hour'),
              (@stale,  'sp-stale',  '\x00', '{}', 'failed', now() - interval '25 hours'),
              (@reauth, 'sp-reauth', '\x00', '{}', 'reauth_required', NULL);
            """,
            new { recent, stale, reauth });

        await new ScheduleSpotifySyncsJob(_db, Options.Create(FakeSpotify.Options()), NullLogger<ScheduleSpotifySyncsJob>.Instance)
            .RunAsync(new ClaimedJob(1, "spotify.schedule_syncs", "{}", 1, 10), CancellationToken.None);

        var queued = (await connection.QueryAsync<Guid>(
            "SELECT (payload->>'user_id')::uuid FROM jobs WHERE kind = 'spotify.sync' AND status = 'queued'")).ToHashSet();
        Assert.Equal(new HashSet<Guid> { _userId, stale }, queued);
        Assert.Equal("queued", await connection.ExecuteScalarAsync<string>(
            "SELECT sync_state FROM spotify_connections WHERE user_id = @stale", new { stale }));
    }

    private async Task RunAsync(int attempt = 1)
    {
        var job = new SpotifySyncJob(_db, _spotify.Client(), _protector, Options.Create(FakeSpotify.Options()),
            NullLogger<SpotifySyncJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, SpotifySyncJob.JobKind, JsonSerializer.Serialize(new { user_id = _userId }), attempt, 10),
            CancellationToken.None);
    }

    private sealed record SignalRow(
        Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, short? TopArtistRank, int RecentPlays);

    private async Task<List<SignalRow>> SignalsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<SignalRow>(
            """
            SELECT artist_id AS ArtistId, liked_tracks AS LikedTracks, playlist_tracks AS PlaylistTracks,
                   distinct_albums AS DistinctAlbums, top_artist_rank AS TopArtistRank, recent_plays AS RecentPlays
            FROM user_artist_signals WHERE user_id = @_userId
            """,
            new { _userId })];
    }

    private async Task<List<string>> TrackLikesAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<string>(
            """
            SELECT t.spotify_track_id || ':' || l.source FROM user_track_likes l JOIN tracks t ON t.id = l.track_id
            WHERE l.user_id = @_userId ORDER BY 1
            """,
            new { _userId })];
    }

    private sealed record StateRow(string SyncState, string? SyncError, int TrackedArtistsFound, bool Synced);

    private async Task<StateRow> StateAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<StateRow>(
            """
            SELECT sync_state AS SyncState, sync_error AS SyncError, tracked_artists_found AS TrackedArtistsFound,
                   last_synced_at IS NOT NULL AS Synced
            FROM spotify_connections WHERE user_id = @_userId
            """,
            new { _userId });
    }

    private async Task<string> StoredTokenAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        var stored = await connection.ExecuteScalarAsync<byte[]>(
            "SELECT refresh_token_enc FROM spotify_connections WHERE user_id = @_userId", new { _userId });
        return _protector.Unprotect(_userId, stored!);
    }
}
