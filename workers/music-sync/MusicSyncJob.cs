using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FandomScore.MusicProviders;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.MusicSync;

/// <summary>
/// Syncs one fan's Apple Music or Tidal signals for tracked, active artists (payload <c>{"user_id", "provider"}</c>,
/// queued by <c>request_music_sync()</c>). Reads the library with the stored token (Tidal: refreshed first and the
/// rotated token saved at once; Apple Music: the Music User Token as is), matches it to the catalog
/// (<see cref="MusicMatcher"/>), then replaces this provider's rows in <c>user_artist_signals</c>,
/// <c>user_track_likes</c>, and <c>user_album_saves</c> in one transaction (migration 0058; Spotify's rows are untouched).
/// Codes learned from title matches are written to <c>tracks.isrc</c> and <c>albums.upc</c>. Idempotent.
/// </summary>
public sealed partial class MusicSyncJob(
    NpgsqlDataSource db, AppleMusicClient appleMusic, TidalClient tidal, SpotifyTokenProtector protector,
    IOptions<MusicProviderOptions> options, ILogger<MusicSyncJob> logger) : IJobHandler
{
    public const string JobKind = "music.sync";

    public string Kind => JobKind;

    private sealed record Connection(string? ProviderUserId, byte[] TokenEnc);

    private sealed record CatalogRow(Guid ArtistId, string ArtistName, Guid? TrackId, string? TrackTitle, string? Isrc, Guid? AlbumId, string? AlbumTitle, string? Upc, string? SpotifyTrackId);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        var userId = payload.RootElement.GetProperty("user_id").GetGuid();
        var provider = payload.RootElement.GetProperty("provider").GetString() ?? "";
        if (!MusicProvider.IsKnown(provider))
        {
            throw new InvalidOperationException($"Unknown music provider '{provider}'.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var lockKey = $"music.sync:{provider}:{userId}";
        if (!await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@lockKey, 0))", new { lockKey }))
        {
            throw new InvalidOperationException("Another sync of this connection is running.");
        }

        try
        {
            await SyncAsync(connection, userId, provider, job, cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@lockKey, 0))", new { lockKey });
        }
    }

    private async Task SyncAsync(NpgsqlConnection connection, Guid userId, string provider, ClaimedJob job, CancellationToken cancellationToken)
    {
        var start = await connection.QuerySingleOrDefaultAsync<Connection>(
            """
            UPDATE music_connections SET sync_state = 'running', last_sync_attempt_at = now()
            WHERE user_id = @userId AND provider = @provider AND sync_state <> 'reauth_required'
            RETURNING provider_user_id AS ProviderUserId, token_enc AS TokenEnc
            """,
            new { userId, provider });
        if (start is null)
        {
            return;
        }

        if (!options.Value.IsEnabled(provider))
        {
            LogNotConfigured(logger, provider);
            await SetStateAsync(connection, userId, provider, "failed", "internal_error");
            return;
        }

        try
        {
            var token = protector.Unprotect(userId, start.TokenEnc, MusicProvider.TokenPurpose(provider));
            MusicLibrary library;
            if (provider == MusicProvider.Tidal)
            {
                var tokens = await tidal.RefreshAsync(token, cancellationToken);
                // Store at once: Tidal may have rotated the refresh token. Also moves it to the current key.
                await connection.ExecuteAsync(
                    "UPDATE music_connections SET token_enc = @token WHERE user_id = @userId AND provider = @provider",
                    new { userId, provider, token = protector.Protect(userId, tokens.RefreshToken ?? token, MusicProvider.TokenPurpose(provider)) });
                library = await tidal.GetLibraryAsync(tokens.AccessToken, cancellationToken);
            }
            else
            {
                library = await appleMusic.GetLibraryAsync(token, cancellationToken);
            }

            var catalog = await LoadCatalogAsync(connection);
            var matched = MusicMatcher.Reduce(library, catalog);
            if (await SaveAsync(connection, userId, provider, matched, cancellationToken))
            {
                LogSynced(logger, provider, userId, matched.Artists.Count, matched.TrackIsrcs.Count);
            }
        }
        catch (MusicAuthorizationException)
        {
            LogReauthRequired(logger, provider, userId);
            await SetStateAsync(connection, userId, provider, "reauth_required", "provider_reauth_required");
        }
        catch (CryptographicException e)
        {
            LogUnreadableToken(logger, e, provider, userId);
            await SetStateAsync(connection, userId, provider, "reauth_required", "provider_reauth_required");
        }
        catch (MusicRequestException e) when (e.IsForbidden)
        {
            LogForbidden(logger, provider, userId);
            await SetStateAsync(connection, userId, provider, "failed", "provider_forbidden");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SetStateAsync(connection, userId, provider, "queued", null);
            throw;
        }
        catch (Exception e)
        {
            var giveUp = job.Attempts >= job.MaxAttempts;
            await SetStateAsync(connection, userId, provider, giveUp ? "failed" : "queued",
                e is MusicUnavailableException ? "provider_unavailable" : "internal_error");
            throw;
        }
    }

    /// <summary>Every active tracked artist with its live catalog, for matching by name, code, and title.</summary>
    private static async Task<List<CatalogArtist>> LoadCatalogAsync(NpgsqlConnection connection)
    {
        var rows = await connection.QueryAsync<CatalogRow>(
            """
            SELECT a.id AS ArtistId, a.name AS ArtistName, t.id AS TrackId, t.title AS TrackTitle, t.isrc AS Isrc,
                   al.id AS AlbumId, al.title AS AlbumTitle, al.upc AS Upc, t.spotify_track_id AS SpotifyTrackId
            FROM artists a
            LEFT JOIN albums al ON al.artist_id = a.id AND al.delisted_at IS NULL
            LEFT JOIN tracks t ON t.album_id = al.id AND t.delisted_at IS NULL
            WHERE a.status = 'active'
            """);
        return [.. rows.GroupBy(r => (r.ArtistId, r.ArtistName)).Select(g => new CatalogArtist(
            g.Key.ArtistId, g.Key.ArtistName,
            [.. g.Where(r => r.TrackId is not null).Select(r => new CatalogTrack(r.TrackId!.Value, r.TrackTitle!, r.Isrc, r.AlbumId!.Value, r.AlbumTitle!, r.SpotifyTrackId))],
            [.. g.Where(r => r.AlbumId is not null).DistinctBy(r => r.AlbumId).Select(r => new CatalogAlbum(r.AlbumId!.Value, r.AlbumTitle!, r.Upc))]))];
    }

    /// <summary>Stores the provider's rows. Returns false when the connection was removed meanwhile.</summary>
    private static async Task<bool> SaveAsync(
        NpgsqlConnection connection, Guid userId, string provider, MatchedSignals matched, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM music_connections WHERE user_id = @userId AND provider = @provider FOR UPDATE)",
            new { userId, provider }, transaction);
        if (!current)
        {
            return false;
        }

        var artists = matched.Artists;
        await connection.ExecuteAsync(
            """
            SELECT request_artist_rescore(artist_id) FROM user_artist_signals WHERE user_id = @userId AND provider = @provider;
            INSERT INTO user_artist_signals (user_id, provider, artist_id, liked_tracks, playlist_tracks, distinct_albums,
                                             recent_plays, top_artist_rank_short, playlists_featuring, dedicated_playlists, followed)
            SELECT @userId, @provider, * FROM unnest(@artistIds, @liked, @playlist, @albums, @recent, @ranksShort, @featuring, @dedicated, @followed)
            ON CONFLICT (user_id, provider, artist_id) DO UPDATE
            SET liked_tracks = EXCLUDED.liked_tracks, playlist_tracks = EXCLUDED.playlist_tracks,
                distinct_albums = EXCLUDED.distinct_albums, recent_plays = EXCLUDED.recent_plays,
                top_artist_rank_short = EXCLUDED.top_artist_rank_short, playlists_featuring = EXCLUDED.playlists_featuring,
                dedicated_playlists = EXCLUDED.dedicated_playlists, followed = EXCLUDED.followed, synced_at = now();
            DELETE FROM user_artist_signals WHERE user_id = @userId AND provider = @provider AND artist_id <> ALL (@artistIds);
            SELECT request_artist_rescore(a) FROM unnest(@artistIds) a;

            -- Likes and playlist tracks: rows gone are removed, dates kept, new rows added (as the Spotify sync does).
            DELETE FROM user_track_likes l
            WHERE l.user_id = @userId AND l.provider = @provider
              AND NOT EXISTS (SELECT 1 FROM unnest(@trackIds, @sources) AS x (track_id, source) WHERE x.track_id = l.track_id AND x.source = l.source);
            UPDATE user_track_likes l SET saved_at = COALESCE(x.saved_at, l.saved_at)
            FROM unnest(@trackIds, @sources, @savedAt) AS x (track_id, source, saved_at)
            WHERE l.user_id = @userId AND l.provider = @provider AND l.track_id = x.track_id AND l.source = x.source;
            INSERT INTO user_track_likes (user_id, provider, track_id, source, saved_at)
            SELECT @userId, @provider, * FROM unnest(@trackIds, @sources, @savedAt)
            ON CONFLICT DO NOTHING;

            DELETE FROM user_album_saves WHERE user_id = @userId AND provider = @provider;
            INSERT INTO user_album_saves (user_id, provider, album_id, saved_at)
            SELECT @userId, @provider, * FROM unnest(@albumIds, @albumSavedAt)
            ON CONFLICT DO NOTHING;

            -- Codes learned from title matches, so the next match from any provider goes by code.
            UPDATE tracks t SET isrc = x.code FROM unnest(@isrcTrackIds, @isrcs) AS x (id, code) WHERE t.id = x.id AND t.isrc IS NULL;
            UPDATE albums a SET upc = x.code FROM unnest(@upcAlbumIds, @upcs) AS x (id, code) WHERE a.id = x.id AND a.upc IS NULL;

            UPDATE music_connections
            SET last_synced_at = now(), tracked_artists_found = @found, sync_error = NULL,
                sync_state = CASE WHEN sync_state = 'running' THEN 'idle' ELSE sync_state END
            WHERE user_id = @userId AND provider = @provider;
            """,
            new
            {
                userId,
                provider,
                artistIds = artists.Select(a => a.ArtistId).ToArray(),
                liked = artists.Select(a => a.LikedTracks).ToArray(),
                playlist = artists.Select(a => a.PlaylistTracks).ToArray(),
                albums = artists.Select(a => a.DistinctAlbums).ToArray(),
                recent = artists.Select(a => a.RecentPlays).ToArray(),
                ranksShort = artists.Select(a => a.TopArtistRankShort).ToArray(),
                featuring = artists.Select(a => a.PlaylistsFeaturing).ToArray(),
                dedicated = artists.Select(a => a.DedicatedPlaylists).ToArray(),
                followed = artists.Select(a => a.Followed).ToArray(),
                trackIds = matched.Tracks.Select(t => t.TrackId).ToArray(),
                sources = matched.Tracks.Select(t => t.Source).ToArray(),
                savedAt = matched.Tracks.Select(t => t.SavedAt).ToArray(),
                albumIds = matched.Albums.Select(a => a.AlbumId).ToArray(),
                albumSavedAt = matched.Albums.Select(a => a.SavedAt).ToArray(),
                isrcTrackIds = matched.TrackIsrcs.Select(c => c.Id).ToArray(),
                isrcs = matched.TrackIsrcs.Select(c => c.Code).ToArray(),
                upcAlbumIds = matched.AlbumUpcs.Select(c => c.Id).ToArray(),
                upcs = matched.AlbumUpcs.Select(c => c.Code).ToArray(),
                found = artists.Count,
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task SetStateAsync(NpgsqlConnection connection, Guid userId, string provider, string state, string? error) =>
        await connection.ExecuteAsync(
            "UPDATE music_connections SET sync_state = @state, sync_error = @error WHERE user_id = @userId AND provider = @provider AND sync_state = 'running'",
            new { userId, provider, state, error });

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced {Provider} for user {UserId}: {Artists} tracked artists found, {Codes} ISRCs learned.")]
    private static partial void LogSynced(ILogger logger, string provider, Guid userId, int artists, int codes);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} no longer accepts user {UserId}'s connection; they must reconnect.")]
    private static partial void LogReauthRequired(ILogger logger, string provider, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not decrypt user {UserId}'s {Provider} token; they must reconnect. Check Spotify:TokenKey and Spotify:PreviousTokenKeys.")]
    private static partial void LogUnreadableToken(ILogger logger, Exception exception, string provider, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} refused access for user {UserId} (403).")]
    private static partial void LogForbidden(ILogger logger, string provider, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Provider} sync is not configured (MusicProviders section); sync marked failed.")]
    private static partial void LogNotConfigured(ILogger logger, string provider);
}
