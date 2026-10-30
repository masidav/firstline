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
    public async Task Sync_stores_the_wider_signals_album_saves_and_seeds_the_play_ledger()
    {
        _spotify.TopArtistIds.AddRange([ArtistX, ArtistA]);
        _spotify.TopArtistIdsShort.AddRange([ArtistA]);
        _spotify.TopArtistIdsLong.AddRange([ArtistX, ArtistB, ArtistA]);
        _spotify.TopTracks.AddRange([
            FakeSpotify.Track("tr-a1", "al-a1", ArtistA), FakeSpotify.Track("tr-a9", "al-a9", ArtistA), FakeSpotify.Track("tr-x1", "al-x1", ArtistX),
        ]);
        _spotify.SavedAlbums.AddRange([
            FakeSpotify.SavedAlbum("al-a1", "2025-05-05T00:00:00Z", ArtistA),   // in the catalog
            FakeSpotify.SavedAlbum("al-a7", "2025-06-06T00:00:00Z", ArtistA),   // not in the catalog: not stored
            FakeSpotify.SavedAlbum("al-x1", "2025-07-07T00:00:00Z", ArtistX),
        ]);
        _spotify.FollowedArtistIds.AddRange([ArtistA, ArtistX]);
        _spotify.Playlists.AddRange([FakeSpotify.Playlist("pl-all-a", "sp-user-1"), FakeSpotify.Playlist("pl-mixed", "sp-user-1")]);
        // Five A tracks: dedicated. One A track among three: featuring only.
        _spotify.PlaylistItems["pl-all-a"] = [.. Enumerable.Range(1, 5).Select(i => FakeSpotify.PlaylistItem(FakeSpotify.Track($"tr-a{i}", "al-a1", ArtistA)))];
        _spotify.PlaylistItems["pl-mixed"] = [
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a1", "al-a1", ArtistA)),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-x1", "al-x1", ArtistX)),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-x2", "al-x2", ArtistX)),
        ];
        _spotify.RecentlyPlayed.AddRange([
            FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA), "2026-10-20T10:00:00Z"),
            FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA), "2026-10-20T09:00:00Z"),
            FakeSpotify.Played(FakeSpotify.Track("tr-x1", "al-x1", ArtistX), "2026-10-20T11:00:00Z"),
        ]);

        await RunAsync();

        var signals = Assert.Single(await SignalsAsync());
        Assert.Equal(((short?)2, (short?)1, (short?)3, 2, 2, 1, true),
            (signals.TopArtistRank, signals.TopArtistRankShort, signals.TopArtistRankLong, signals.TopTracks,
             signals.PlaylistsFeaturing, signals.DedicatedPlaylists, signals.Followed));
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(["al-a1:2025-05-05"], await connection.QueryAsync<string>(
            """
            SELECT al.spotify_album_id || ':' || to_char(s.saved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD')
            FROM user_album_saves s JOIN albums al ON al.id = s.album_id WHERE s.user_id = @_userId
            """, new { _userId }));
        // Only A's plays enter the ledger; the cursor is the newest play Spotify returned for A.
        Assert.Equal(["2026-10-20T09:00:00Z", "2026-10-20T10:00:00Z"], await connection.QueryAsync<string>(
            "SELECT to_char(played_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"') FROM user_plays WHERE user_id = @_userId ORDER BY played_at", new { _userId }));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            await connection.ExecuteScalarAsync<long>("SELECT plays_cursor FROM spotify_connections WHERE user_id = @_userId", new { _userId }));
        Assert.Contains(_spotify.Calls, c => c.Path == "me/following");
        Assert.Equal(3, _spotify.Calls.Count(c => c.Path == "me/top/artists"));
        Assert.Equal(3, _spotify.Calls.Count(c => c.Path == "me/top/tracks"));
    }

    [Fact]
    public async Task Playlist_adds_and_the_follow_are_dated_and_keep_their_dates_across_syncs()
    {
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tracks (album_id, title, spotify_track_id)
                SELECT id, 'Track ' || x, x FROM albums, unnest(ARRAY['tr-a2', 'tr-a3']) x WHERE spotify_album_id = 'al-a1'
                """);
        }

        // tr-a1 has Spotify's added_at; tr-a2 has none (a very old playlist); tr-a3 is in two playlists, the later add first.
        _spotify.Playlists.AddRange([FakeSpotify.Playlist("pl-one", "sp-user-1"), FakeSpotify.Playlist("pl-two", "sp-user-1")]);
        _spotify.PlaylistItems["pl-one"] = [
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a1", "al-a1", ArtistA), "2026-10-20T10:00:00Z"),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a2", "al-a1", ArtistA)),
            FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a3", "al-a1", ArtistA), "2026-10-22T10:00:00Z"),
        ];
        _spotify.PlaylistItems["pl-two"] = [FakeSpotify.PlaylistItem(FakeSpotify.Track("tr-a3", "al-a1", ArtistA), "2026-10-21T10:00:00Z")];
        _spotify.FollowedArtistIds.Add(ArtistA);

        var before = DateTime.UtcNow.AddSeconds(-1);
        await RunAsync();
        var first = await PlaylistAddsAsync();
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc), first["tr-a1"]);
        Assert.Equal(new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc), first["tr-a3"]);   // the earliest add
        Assert.InRange(first["tr-a2"], before, DateTime.UtcNow.AddSeconds(1));                  // first seen by this sync
        var followedSince = Assert.Single(await FollowedSinceAsync()).Value;
        Assert.NotNull(followedSince);
        Assert.InRange(followedSince.Value, before, DateTime.UtcNow.AddSeconds(1));

        // The daily re-sync changes nothing it already knew.
        await Task.Delay(20);
        await RunAsync();
        Assert.Equal(first, await PlaylistAddsAsync());
        Assert.Equal(followedSince, Assert.Single(await FollowedSinceAsync()).Value);

        // Unfollowing clears the date; following again dates it afresh. A track taken out of every playlist is gone.
        _spotify.FollowedArtistIds.Clear();
        _spotify.PlaylistItems["pl-one"].RemoveAt(1);
        await RunAsync();
        Assert.Null(Assert.Single(await FollowedSinceAsync()).Value);
        Assert.Equal(["tr-a1", "tr-a3"], (await PlaylistAddsAsync()).Keys.Order());
        await Task.Delay(20);
        _spotify.FollowedArtistIds.Add(ArtistA);
        await RunAsync();
        var again = Assert.Single(await FollowedSinceAsync()).Value;
        Assert.NotNull(again);
        Assert.True(again > followedSince, $"{again:O} should be after {followedSince:O}");
    }

    [Fact]
    public async Task Follows_are_skipped_for_connections_made_without_the_follow_scope()
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE spotify_connections SET scopes = array_remove(scopes, @scope) WHERE user_id = @_userId",
            new { _userId, scope = SpotifyOptions.FollowScope });
        _spotify.FollowedArtistIds.Add(ArtistA);
        _spotify.TopArtistIds.Add(ArtistA);

        await RunAsync();

        Assert.False(Assert.Single(await SignalsAsync()).Followed);
        Assert.DoesNotContain(_spotify.Calls, c => c.Path == "me/following");
        Assert.Equal("idle", (await StateAsync()).SyncState);
    }

    [Fact]
    public async Task Polling_extends_the_play_ledger_from_the_cursor_and_prunes_old_plays()
    {
        _spotify.RecentlyPlayed.Add(FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA), "2026-10-20T10:00:00Z"));
        await RunAsync();   // seeds one play and the cursor
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at) VALUES (@_userId, @_artistA, 'tr-a1', now() - interval '500 days')",
            new { _userId, _artistA });
        _spotify.RecentlyPlayed.Clear();
        _spotify.RecentlyPlayed.AddRange([
            FakeSpotify.Played(FakeSpotify.Track("tr-a1", "al-a1", ArtistA), "2026-10-20T10:00:00Z"),   // already stored (cursor)
            FakeSpotify.Played(FakeSpotify.Track("tr-a2", "al-a2", ArtistA), "2026-10-21T08:00:00Z"),
            FakeSpotify.Played(FakeSpotify.Track("tr-x1", "al-x1", ArtistX), "2026-10-21T09:00:00Z"),   // untracked: dropped, moves the cursor
        ]);
        await connection.ExecuteAsync("DELETE FROM jobs WHERE kind = 'scoring.score_artist'");

        var job = new PollPlaysJob(_db, _spotify.Client(), _protector, Options.Create(FakeSpotify.Options()), NullLogger<PollPlaysJob>.Instance);
        await job.RunAsync(new ClaimedJob(2, PollPlaysJob.JobKind, JsonSerializer.Serialize(new { user_id = _userId }), 1, 10), CancellationToken.None);

        Assert.Equal(["tr-a1", "tr-a2"], await connection.QueryAsync<string>(
            "SELECT spotify_track_id FROM user_plays WHERE user_id = @_userId ORDER BY played_at", new { _userId }));
        var state = await connection.QuerySingleAsync<(long Cursor, DateTime? PolledAt)>(
            "SELECT plays_cursor, plays_polled_at FROM spotify_connections WHERE user_id = @_userId", new { _userId });
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), state.Cursor);
        Assert.NotNull(state.PolledAt);
        var poll = Assert.Single(_spotify.Calls, c => c.Path == "me/player/recently-played" && c.Query.Contains("after=", StringComparison.Ordinal));
        Assert.Contains($"after={new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()}", poll.Query, StringComparison.Ordinal);
        // New plays queue a rescore of that artist.
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued' AND payload->>'artist_id' = @id", new { id = _artistA.ToString() }));
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
        // Play polls go to connections that have synced before and were not polled within the hour.
        await connection.ExecuteAsync(
            """
            -- Both synced an hour ago (not due for a sync), never polled.
            UPDATE spotify_connections SET sync_state = 'idle', last_sync_attempt_at = now() - interval '1 hour',
                   last_synced_at = now() - interval '1 hour', plays_polled_at = NULL
             WHERE user_id IN (@recent, @stale);
            UPDATE spotify_connections SET last_synced_at = now(), plays_polled_at = now() - interval '10 minutes' WHERE user_id = @reauth;
            """, new { recent, stale, reauth });
        await new ScheduleSpotifySyncsJob(_db, Options.Create(FakeSpotify.Options()), NullLogger<ScheduleSpotifySyncsJob>.Instance)
            .RunAsync(new ClaimedJob(2, "spotify.schedule_syncs", "{}", 1, 10), CancellationToken.None);
        var polls = (await connection.QueryAsync<Guid>(
            "SELECT (payload->>'user_id')::uuid FROM jobs WHERE kind = 'spotify.poll_plays' AND status = 'queued'")).ToHashSet();
        Assert.Equal(new HashSet<Guid> { recent, stale }, polls);
    }

    [Fact]
    public async Task Top_artists_nobody_tracks_are_added_for_the_fan_and_start_onboarding()
    {
        // X is unknown here; A is tracked already. Needs the client secret, like adding by hand.
        _spotify.Artists.Add(new FakeSpotify.Artist(ArtistX, "Artist X", "indie"));
        _spotify.TopArtistIds.AddRange([ArtistX, ArtistA]);
        var options = FakeSpotify.Options();
        options.ClientSecret = "test-secret";

        await RunAsync(options: options);

        await using var connection = await _db.OpenConnectionAsync();
        var added = await connection.QuerySingleAsync<(string Name, string Status, Guid? AddedBy, string? ImageUrl)>(
            "SELECT name, status::text, added_by, image_url FROM artists WHERE spotify_artist_id = @ArtistX", new { ArtistX });
        Assert.Equal(("Artist X", "onboarding", _userId, $"https://i.scdn.co/{ArtistX}-320"), added);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM jobs WHERE kind = 'artist.onboard' AND payload->>'artist_id' = (SELECT id::text FROM artists WHERE spotify_artist_id = @ArtistX)",
            new { ArtistX }));
        Assert.Equal(1, (await StateAsync()).TopArtistsAdded);
        // Still onboarding, so no signals for X yet; A's rank is stored as before.
        Assert.Equal((short)2, Assert.Single(await SignalsAsync()).TopArtistRank);

        // A second sync finds X tracked and adds nothing.
        await RunAsync(options: options);
        Assert.Equal(0, (await StateAsync()).TopArtistsAdded);
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM artists"));
    }

    [Fact]
    public async Task Auto_tracking_is_capped_and_off_without_the_client_secret()
    {
        _spotify.TopArtistIds.AddRange(["sp-top-1", "sp-top-2", "sp-top-3"]);

        // Without a client secret the artists could never load a catalog, so none is added.
        await RunAsync();
        Assert.Equal(0, (await StateAsync()).TopArtistsAdded);

        var options = FakeSpotify.Options();
        options.ClientSecret = "test-secret";
        options.AutoTrackTopArtists = 2;
        await RunAsync(options: options);
        Assert.Equal(2, (await StateAsync()).TopArtistsAdded);
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(["sp-top-1", "sp-top-2"], await connection.QueryAsync<string>(
            "SELECT spotify_artist_id FROM artists WHERE added_by = @_userId ORDER BY tracked_at, spotify_artist_id", new { _userId }));

        // The next sync continues with the rest, best rank first.
        await RunAsync(options: options);
        Assert.Equal(1, (await StateAsync()).TopArtistsAdded);
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM artists WHERE added_by = @_userId", new { _userId }));
    }

    private async Task RunAsync(int attempt = 1, SpotifyOptions? options = null)
    {
        var job = new SpotifySyncJob(_db, _spotify.Client(options), _protector, Options.Create(options ?? FakeSpotify.Options()),
            NullLogger<SpotifySyncJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, SpotifySyncJob.JobKind, JsonSerializer.Serialize(new { user_id = _userId }), attempt, 10),
            CancellationToken.None);
    }

    private sealed record SignalRow(
        Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, short? TopArtistRank, int RecentPlays,
        short? TopArtistRankShort, short? TopArtistRankLong, int TopTracks, int PlaylistsFeaturing, int DedicatedPlaylists,
        bool Followed);

    private async Task<List<SignalRow>> SignalsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<SignalRow>(
            """
            SELECT artist_id AS ArtistId, liked_tracks AS LikedTracks, playlist_tracks AS PlaylistTracks,
                   distinct_albums AS DistinctAlbums, top_artist_rank AS TopArtistRank, recent_plays AS RecentPlays,
                   top_artist_rank_short AS TopArtistRankShort, top_artist_rank_long AS TopArtistRankLong, top_tracks AS TopTracks,
                   playlists_featuring AS PlaylistsFeaturing, dedicated_playlists AS DedicatedPlaylists, followed AS Followed
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

    /// <summary>Spotify track id → saved_at (UTC) of the fan's playlist rows.</summary>
    private async Task<Dictionary<string, DateTime>> PlaylistAddsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.QueryAsync<(string TrackId, DateTime SavedAt)>(
                """
                SELECT t.spotify_track_id, l.saved_at AT TIME ZONE 'UTC' FROM user_track_likes l JOIN tracks t ON t.id = l.track_id
                WHERE l.user_id = @_userId AND l.source = 'playlist'
                """,
                new { _userId }))
            .ToDictionary(r => r.TrackId, r => DateTime.SpecifyKind(r.SavedAt, DateTimeKind.Utc));
    }

    /// <summary>Artist id → followed_since (UTC, null when not followed) from the fan's signals.</summary>
    private async Task<Dictionary<Guid, DateTime?>> FollowedSinceAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.QueryAsync<(Guid ArtistId, DateTime? FollowedSince)>(
                "SELECT artist_id, followed_since AT TIME ZONE 'UTC' FROM user_artist_signals WHERE user_id = @_userId", new { _userId }))
            .ToDictionary(r => r.ArtistId, DateTime? (r) => r.FollowedSince is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null);
    }

    private sealed record StateRow(string SyncState, string? SyncError, int TrackedArtistsFound, bool Synced, int TopArtistsAdded);

    private async Task<StateRow> StateAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<StateRow>(
            """
            SELECT sync_state AS SyncState, sync_error AS SyncError, tracked_artists_found AS TrackedArtistsFound,
                   last_synced_at IS NOT NULL AS Synced, top_artists_added AS TopArtistsAdded
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
