using System.Net;
using System.Text.Json;
using Dapper;
using FandomScore.Database;
using FandomScore.MusicProviders;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.MusicSync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// The Apple Music and Tidal sync jobs against a real database, with both providers faked (migration 0058, ADR 0007):
/// matching by artist name then ISRC or title, provider rows kept apart from Spotify's, the score reading the
/// strongest signal across providers, learned codes, and the failure states.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MusicSyncJobTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private readonly FakeAppleMusic _apple = new();
    private readonly FakeTidal _tidal = new();
    private readonly SpotifyTokenProtector _protector = new(FakeSpotify.Options());
    private NpgsqlDataSource _db = null!;
    private Guid _userId;
    private Guid _artistA;   // "The National", active, with a catalog
    private Guid _albumA1;
    private Guid _trackA1;   // "Fake Empire" (isrc USXYZ0700001)
    private Guid _trackA2;   // "Mistaken for Strangers" (no isrc yet)

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"music_sync_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        await using var connection = await _db.OpenConnectionAsync();
        _userId = await connection.ExecuteScalarAsync<Guid>("INSERT INTO users (handle, email) VALUES ('fan_one', 'fan@example.test') RETURNING id");
        _artistA = await ScoringSetup.ActiveArtistAsync(_db, "sp-national", "The National", releases: 0);
        await connection.ExecuteAsync(
            """
            INSERT INTO artists (name, spotify_artist_id) VALUES ('Wednesday', 'sp-wed-1');
            INSERT INTO artists (name, spotify_artist_id) VALUES ('Wednesday', 'sp-wed-2');
            """);
        _albumA1 = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO albums (artist_id, title, spotify_album_id) VALUES (@_artistA, 'Boxer', 'al-boxer') RETURNING id", new { _artistA });
        _trackA1 = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO tracks (album_id, title, spotify_track_id, isrc) VALUES (@_albumA1, 'Fake Empire', 'tr-fake-empire', 'USXYZ0700001') RETURNING id", new { _albumA1 });
        _trackA2 = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO tracks (album_id, title, spotify_track_id) VALUES (@_albumA1, 'Mistaken for Strangers', 'tr-mistaken') RETURNING id", new { _albumA1 });
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose()
    {
        _apple.Dispose();
        _tidal.Dispose();
    }

    [Fact]
    public async Task Apple_music_sync_matches_by_isrc_and_title_keeps_only_tracked_artists_and_learns_codes()
    {
        await ConnectAsync(MusicProvider.AppleMusic, "mut-1");
        _apple.LibrarySongs.AddRange([
            FakeAppleMusic.Song("ls-1", "Fake Empire (Live)", "The National", "Boxer", isrc: "USXYZ0700001", dateAdded: "2026-01-02T03:04:05Z"),   // by code
            FakeAppleMusic.Song("ls-2", "Mistaken For Strangers", "The National", "Boxer", isrc: "USXYZ0700002"),                           // by title; code learned
            FakeAppleMusic.Song("ls-3", "Fake Empire", "The National", "Boxer"),                                                             // same track again (an upload)
            FakeAppleMusic.Song("ls-4", "Bull Believer", "Wednesday", "Rat Saw God", isrc: "USWED0000001"),                                 // ambiguous artist name: dropped
            FakeAppleMusic.Song("ls-5", "Hey Jude", "The Beatles", "Past Masters", isrc: "GBAYE0601690"),                                   // untracked: dropped
        ]);
        _apple.LibraryAlbums.Add(FakeAppleMusic.Album("la-1", "Boxer (Deluxe Edition)", "The National", dateAdded: "2026-02-03T00:00:00Z"));
        _apple.LibraryPlaylists.Add(FakeAppleMusic.Playlist("lp-1", "Driving"));
        _apple.PlaylistTracks["lp-1"] = [
            FakeAppleMusic.Song("ls-1", "Fake Empire", "The National", "Boxer", isrc: "USXYZ0700001"),
            FakeAppleMusic.Song("ls-2", "Mistaken for Strangers", "The National", "Boxer"),
            FakeAppleMusic.Song("ls-5", "Hey Jude", "The Beatles", "Past Masters"),
        ];
        _apple.RecentlyPlayed.AddRange([
            FakeAppleMusic.CatalogSong("cat-1", "Fake Empire", "The National", "USXYZ0700001"),
            FakeAppleMusic.CatalogSong("cat-1", "Fake Empire", "The National", "USXYZ0700001"),
        ]);
        _apple.HeavyRotation.AddRange([
            FakeAppleMusic.HeavyRotationAlbum("hr-1", "Past Masters", "The Beatles"),
            FakeAppleMusic.HeavyRotationAlbum("hr-2", "Boxer", "The National"),
        ]);

        await RunAsync(MusicProvider.AppleMusic);

        var signals = Assert.Single(await SignalsAsync(MusicProvider.AppleMusic));
        Assert.Equal((_artistA, 2, 2, 1, 2, (short)2, 1, 0, false),
            (signals.ArtistId, signals.LikedTracks, signals.PlaylistTracks, signals.DistinctAlbums, signals.RecentPlays,
             signals.TopArtistRankShort, signals.PlaylistsFeaturing, signals.DedicatedPlaylists, signals.Followed));
        // A playlist row Apple doesn't date is stamped when first seen (migration 0048), as Spotify's are.
        Assert.Equal(Sorted([(_trackA1, "liked", "2026-01-02T03:04:05"), (_trackA1, "playlist", "stamped"), (_trackA2, "liked", "none"), (_trackA2, "playlist", "stamped")]),
            await TrackLikesAsync(MusicProvider.AppleMusic));
        Assert.Equal([(_albumA1, "2026-02-03T00:00:00")], await AlbumSavesAsync(MusicProvider.AppleMusic));

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("USXYZ0700002", await connection.ExecuteScalarAsync<string>("SELECT isrc FROM tracks WHERE id = @_trackA2", new { _trackA2 }));
        var state = await StateAsync(MusicProvider.AppleMusic);
        Assert.Equal(("idle", (string?)null, 1, true), (state.SyncState, state.SyncError, state.TrackedArtistsFound, state.Synced));
        Assert.Equal([_artistA.ToString()], await connection.QueryAsync<string>(
            "SELECT payload->>'artist_id' FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued'"));
        // Every Apple request carried the developer token and the fan's token; nothing about other artists was stored.
        Assert.All(_apple.Calls, c => Assert.Equal(("mut-1", true), (c.UserToken, c.Bearer?.StartsWith("eyJ", StringComparison.Ordinal))));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM user_track_likes WHERE track_id NOT IN (@_trackA1, @_trackA2)", new { _trackA1, _trackA2 }));
    }

    [Fact]
    public async Task Tidal_sync_reads_the_collection_with_artists_from_the_lookups_and_counts_favourite_artists_as_follows()
    {
        await ConnectAsync(MusicProvider.Tidal, "tidal-refresh-0", providerUserId: "td-user-1");
        var national = new FakeTidal.Artist("ta-1", "The National");
        var beatles = new FakeTidal.Artist("ta-2", "The Beatles");
        _tidal.CollectionTracks.AddRange([
            (new FakeTidal.Track("tt-1", "Fake Empire", "USXYZ0700001", national), "2025-12-01T00:00:00Z"),
            (new FakeTidal.Track("tt-2", "Hey Jude", "GBAYE0601690", beatles), null),
        ]);
        _tidal.CollectionAlbums.Add((new FakeTidal.Album("tal-1", "Boxer", "0123456789012", national), "2025-12-02T00:00:00Z"));
        _tidal.CollectionArtists.AddRange([national, beatles]);
        _tidal.Playlists["tp-1"] = [(new FakeTidal.Track("tt-3", "Mistaken for Strangers", null, national), "2025-12-03T00:00:00Z")];

        await RunAsync(MusicProvider.Tidal);

        var signals = Assert.Single(await SignalsAsync(MusicProvider.Tidal));
        Assert.Equal((_artistA, 1, 1, 1, 0, (short?)null, 1, 0, true),
            (signals.ArtistId, signals.LikedTracks, signals.PlaylistTracks, signals.DistinctAlbums, signals.RecentPlays,
             signals.TopArtistRankShort, signals.PlaylistsFeaturing, signals.DedicatedPlaylists, signals.Followed));
        Assert.Equal(Sorted([(_trackA1, "liked", "2025-12-01T00:00:00"), (_trackA2, "playlist", "2025-12-03T00:00:00")]), await TrackLikesAsync(MusicProvider.Tidal));
        Assert.Equal([(_albumA1, "2025-12-02T00:00:00")], await AlbumSavesAsync(MusicProvider.Tidal));

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("0123456789012", await connection.ExecuteScalarAsync<string>("SELECT upc FROM albums WHERE id = @_albumA1", new { _albumA1 }));
        // The rotated refresh token was stored before anything else, under Tidal's own purpose.
        var stored = await connection.ExecuteScalarAsync<byte[]>("SELECT token_enc FROM music_connections WHERE provider = 'tidal'");
        Assert.Equal("tidal-refresh-1", _protector.Unprotect(_userId, stored!, MusicProvider.TokenPurpose(MusicProvider.Tidal)));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => _protector.Unprotect(_userId, stored!));
        var refresh = Assert.Single(_tidal.Calls, c => c.Path == "token").Form;
        Assert.Equal(("refresh_token", "tidal-refresh-0", FakeTidal.ClientId), (refresh["grant_type"], refresh["refresh_token"], refresh["client_id"]));
    }

    [Fact]
    public async Task Providers_keep_their_own_rows_and_the_score_reads_the_strongest_signal_across_them()
    {
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // The fan's Spotify rows, as the Spotify sync would leave them.
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks, top_artist_rank_short, followed) VALUES (@_userId, @_artistA, 5, 1, false);
                INSERT INTO user_track_likes (user_id, track_id, source, saved_at) VALUES (@_userId, @_trackA1, 'liked', '2024-01-01T00:00:00Z');
                INSERT INTO user_album_saves (user_id, album_id, saved_at) VALUES (@_userId, @_albumA1, '2024-01-01T00:00:00Z');
                """,
                new { _userId, _artistA, _trackA1, _albumA1 });
        }

        await ConnectAsync(MusicProvider.AppleMusic, "mut-1");
        _apple.LibrarySongs.AddRange([
            FakeAppleMusic.Song("ls-1", "Fake Empire", "The National", "Boxer", isrc: "USXYZ0700001"),
            FakeAppleMusic.Song("ls-2", "Mistaken for Strangers", "The National", "Boxer"),
        ]);
        _apple.LibraryAlbums.Add(FakeAppleMusic.Album("la-1", "Boxer", "The National"));
        _apple.HeavyRotation.AddRange([FakeAppleMusic.HeavyRotationAlbum("hr-1", "Boxer", "The National")]);
        await RunAsync(MusicProvider.AppleMusic);

        await using var db = await _db.OpenConnectionAsync();
        var rows = (await db.QueryAsync<(string Provider, int Liked)>(
            "SELECT provider, liked_tracks FROM user_artist_signals WHERE user_id = @_userId ORDER BY provider", new { _userId })).ToList();
        Assert.Equal([("apple_music", 2), ("spotify", 5)], rows);
        var score = await db.QuerySingleAsync<(int Liked, short? RankShort, int SavedAlbums, int Touched, DateTime FirstSaved)>(
            """
            SELECT liked_tracks, top_artist_rank_short, saved_albums, tracks_touched, first_saved_at
            FROM v_raw_scores WHERE user_id = @_userId AND artist_id = @_artistA
            """,
            new { _userId, _artistA });
        // Max of the counts (5, not 7), the best rank (1), one distinct album (not 2), two distinct tracks touched.
        Assert.Equal((5, (short)1, 1, 2, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), score);

        // Disconnecting Apple Music removes only its rows.
        await db.ExecuteAsync(
            """
            DELETE FROM music_connections WHERE user_id = @_userId AND provider = 'apple_music';
            DELETE FROM user_artist_signals WHERE user_id = @_userId AND provider = 'apple_music';
            DELETE FROM user_track_likes WHERE user_id = @_userId AND provider = 'apple_music';
            DELETE FROM user_album_saves WHERE user_id = @_userId AND provider = 'apple_music';
            """,
            new { _userId });
        Assert.Equal(5, await db.ExecuteScalarAsync<int>("SELECT liked_tracks FROM v_raw_scores WHERE user_id = @_userId AND artist_id = @_artistA", new { _userId, _artistA }));
    }

    [Fact]
    public async Task A_rejected_apple_token_marks_the_connection_reauth_required_and_a_provider_outage_retries()
    {
        await ConnectAsync(MusicProvider.AppleMusic, "mut-revoked");
        await RunAsync(MusicProvider.AppleMusic);
        var state = await StateAsync(MusicProvider.AppleMusic);
        Assert.Equal(("reauth_required", "provider_reauth_required"), (state.SyncState, state.SyncError));

        await ConnectAsync(MusicProvider.Tidal, "tidal-refresh-0", providerUserId: "td-user-1");
        _tidal.ApiStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<MusicUnavailableException>(() => RunAsync(MusicProvider.Tidal, attempt: 1));
        Assert.Equal(("queued", "provider_unavailable"), ((await StateAsync(MusicProvider.Tidal)).SyncState, (await StateAsync(MusicProvider.Tidal)).SyncError));
        await Assert.ThrowsAsync<MusicUnavailableException>(() => RunAsync(MusicProvider.Tidal, attempt: 10));
        Assert.Equal("failed", (await StateAsync(MusicProvider.Tidal)).SyncState);
    }

    [Fact]
    public async Task The_schedule_queues_due_connections_of_configured_providers_only()
    {
        await ConnectAsync(MusicProvider.AppleMusic, "mut-1");
        await ConnectAsync(MusicProvider.Tidal, "tidal-refresh-0", providerUserId: "td-user-1");
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE music_connections SET sync_state = 'idle'; DELETE FROM jobs WHERE kind = 'music.sync'");

        var options = FakeAppleMusic.Options();
        options.Tidal.ClientId = "";   // Tidal not set up: its connection is left alone
        await new ScheduleMusicSyncsJob(_db, Options.Create(options), NullLogger<ScheduleMusicSyncsJob>.Instance)
            .RunAsync(new ClaimedJob(1, "music.schedule_syncs", "{}", 1, 10), CancellationToken.None);

        Assert.Equal(["apple_music"], await connection.QueryAsync<string>("SELECT payload->>'provider' FROM jobs WHERE kind = 'music.sync' AND status = 'queued'"));
        Assert.Equal("queued", await connection.ExecuteScalarAsync<string>("SELECT sync_state FROM music_connections WHERE provider = 'apple_music'"));
        Assert.Equal("idle", await connection.ExecuteScalarAsync<string>("SELECT sync_state FROM music_connections WHERE provider = 'tidal'"));
    }

    [Theory]
    [InlineData("Fake Empire (Live at the Beacon)", "fakeempire")]
    [InlineData("Mistaken For Strangers - 2011 Remaster", "mistakenforstrangers")]
    [InlineData("Déjà Vu feat. Someone", "dejavu")]
    [InlineData("Florence & the Machine", "florenceandthemachine")]
    [InlineData("The National", "thenational")]
    public void Titles_and_names_are_normalized_before_matching(string value, string expected) =>
        Assert.Equal(expected, MusicMatcher.Normalize(value));

    // ------------------------------------------------------------------ helpers

    private async Task ConnectAsync(string provider, string token, string? providerUserId = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO music_connections (user_id, provider, provider_user_id, token_enc, scopes)
            VALUES (@_userId, @provider, @providerUserId, @token, '{}')
            ON CONFLICT (user_id, provider) DO UPDATE SET token_enc = EXCLUDED.token_enc, sync_state = 'idle', sync_error = NULL;
            SELECT request_music_sync(@_userId, @provider);
            """,
            new { _userId, provider, providerUserId, token = _protector.Protect(_userId, token, MusicProvider.TokenPurpose(provider)) });
    }

    private async Task RunAsync(string provider, int attempt = 1)
    {
        var job = new MusicSyncJob(_db, _apple.Client(), _tidal.Client(), _protector, Options.Create(FakeAppleMusic.Options()),
            NullLogger<MusicSyncJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, MusicSyncJob.JobKind, JsonSerializer.Serialize(new { user_id = _userId, provider }), attempt, 10),
            CancellationToken.None);
    }

    private sealed record SignalsRow(Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, int RecentPlays,
        short? TopArtistRankShort, int PlaylistsFeaturing, int DedicatedPlaylists, bool Followed);

    private async Task<List<SignalsRow>> SignalsAsync(string provider)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<SignalsRow>(
            """
            SELECT artist_id AS ArtistId, liked_tracks AS LikedTracks, playlist_tracks AS PlaylistTracks, distinct_albums AS DistinctAlbums,
                   recent_plays AS RecentPlays, top_artist_rank_short AS TopArtistRankShort, playlists_featuring AS PlaylistsFeaturing,
                   dedicated_playlists AS DedicatedPlaylists, followed AS Followed
            FROM user_artist_signals WHERE user_id = @_userId AND provider = @provider ORDER BY artist_id
            """,
            new { _userId, provider })];
    }

    private static List<(Guid TrackId, string Source, string SavedAt)> Sorted(IEnumerable<(Guid TrackId, string Source, string SavedAt)> rows) =>
        [.. rows.OrderBy(r => r.Source, StringComparer.Ordinal).ThenBy(r => r.TrackId)];

    /// <summary>Rows by source then track: the save date, "none" when null, "stamped" when the database dated it just now.</summary>
    private async Task<List<(Guid TrackId, string Source, string SavedAt)>> TrackLikesAsync(string provider)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<(Guid, string, string)>(
            """
            SELECT track_id, source,
                   CASE WHEN saved_at IS NULL THEN 'none' WHEN saved_at > now() - interval '1 minute' THEN 'stamped'
                        ELSE to_char(saved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS') END
            FROM user_track_likes WHERE user_id = @_userId AND provider = @provider ORDER BY source, track_id
            """,
            new { _userId, provider })];
    }

    private async Task<List<(Guid AlbumId, string? SavedAt)>> AlbumSavesAsync(string provider)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<(Guid, string?)>(
            "SELECT album_id, to_char(saved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM user_album_saves WHERE user_id = @_userId AND provider = @provider",
            new { _userId, provider })];
    }

    private sealed record State(string SyncState, string? SyncError, int TrackedArtistsFound, bool Synced);

    private async Task<State> StateAsync(string provider)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<State>(
            "SELECT sync_state AS SyncState, sync_error AS SyncError, tracked_artists_found AS TrackedArtistsFound, last_synced_at IS NOT NULL AS Synced FROM music_connections WHERE user_id = @_userId AND provider = @provider",
            new { _userId, provider });
    }
}
