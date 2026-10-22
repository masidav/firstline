using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.SpotifySync;

/// <summary>
/// Syncs one user's Spotify signals for tracked, active artists (payload <c>{"user_id": ...}</c>, queued by
/// <c>request_spotify_sync()</c>). Refreshes the token, reads the library, adds the fan's top artists that nobody
/// tracks yet (<see cref="SpotifyOptions.AutoTrackTopArtists"/>; they onboard and then score on the fan's next
/// sync), drops untracked artists in memory, then replaces the user's <c>user_artist_signals</c> and
/// <c>user_track_likes</c> in one transaction, so running it again gives the same result.
/// See docs/adr/0005-spotify-integration.md.
/// </summary>
public sealed partial class SpotifySyncJob(
    NpgsqlDataSource db, SpotifyClient spotify, SpotifyTokenProtector protector, IOptions<SpotifyOptions> options,
    ILogger<SpotifySyncJob> logger) : IJobHandler
{
    public const string JobKind = "spotify.sync";

    public string Kind => JobKind;

    private sealed record Connection(string SpotifyUserId, byte[] RefreshTokenEnc);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        var userId = payload.RootElement.GetProperty("user_id").GetGuid();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);

        // Two syncs of one user at once would race on the refresh token, which Spotify may rotate.
        var lockKey = $"spotify.sync:{userId}";
        if (!await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@lockKey, 0))", new { lockKey }))
        {
            throw new InvalidOperationException("Another Spotify sync for this user is running.");
        }

        try
        {
            await SyncAsync(connection, userId, job, cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@lockKey, 0))", new { lockKey });
        }
    }

    private async Task SyncAsync(NpgsqlConnection connection, Guid userId, ClaimedJob job, CancellationToken cancellationToken)
    {
        var start = await connection.QuerySingleOrDefaultAsync<Connection>(
            """
            UPDATE spotify_connections SET sync_state = 'running', last_sync_attempt_at = now()
            WHERE user_id = @userId AND sync_state <> 'reauth_required'
            RETURNING spotify_user_id AS SpotifyUserId, refresh_token_enc AS RefreshTokenEnc
            """,
            new { userId });
        if (start is null)
        {
            // Disconnected, or waiting for the user to reconnect.
            return;
        }

        if (!options.Value.IsEnabled)
        {
            LogNotConfigured(logger);
            await SetStateAsync(connection, userId, "failed", "internal_error");
            return;
        }

        try
        {
            var refreshToken = protector.Unprotect(userId, start.RefreshTokenEnc);
            var tokens = await spotify.RefreshAsync(refreshToken, cancellationToken);
            // Store at once: Spotify may have rotated the refresh token. Also moves it to the current key.
            await connection.ExecuteAsync(
                "UPDATE spotify_connections SET refresh_token_enc = @token WHERE user_id = @userId AND spotify_user_id = @spotifyUserId",
                new { userId, start.SpotifyUserId, token = protector.Protect(userId, tokens.RefreshToken ?? refreshToken) });

            var library = await spotify.GetLibraryAsync(tokens.AccessToken, start.SpotifyUserId, cancellationToken);
            var added = await TrackTopArtistsAsync(connection, userId, library.TopArtists, cancellationToken);
            var tracked = (await connection.QueryAsync<(string SpotifyArtistId, Guid Id)>(
                    "SELECT spotify_artist_id, id FROM artists WHERE status = 'active'"))
                .ToDictionary(a => a.SpotifyArtistId, a => a.Id);
            var signals = SpotifySignals.Reduce(library, tracked);

            if (await SaveAsync(connection, userId, start.SpotifyUserId, signals, added, cancellationToken))
            {
                LogSynced(logger, userId, signals.Artists.Count, added);
            }
        }
        catch (SpotifyAuthorizationException)
        {
            // Retrying cannot help; the user must connect again.
            LogReauthRequired(logger, userId);
            await SetStateAsync(connection, userId, "reauth_required", "spotify_reauth_required");
        }
        catch (CryptographicException e)
        {
            // The token key was lost or replaced without keeping the old one in PreviousTokenKeys.
            LogUnreadableToken(logger, e, userId);
            await SetStateAsync(connection, userId, "reauth_required", "spotify_reauth_required");
        }
        catch (SpotifyRequestException e) when (e.IsForbidden)
        {
            // In development mode: the Spotify account is not on the app's allowlist. The schedule tries again later.
            LogForbidden(logger, userId);
            await SetStateAsync(connection, userId, "failed", "spotify_forbidden");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the runner hands the job back to the queue.
            await SetStateAsync(connection, userId, "queued", null);
            throw;
        }
        catch (Exception e)
        {
            var giveUp = job.Attempts >= job.MaxAttempts;
            await SetStateAsync(connection, userId, giveUp ? "failed" : "queued",
                e is SpotifyUnavailableException ? "spotify_unavailable" : "internal_error");
            throw;
        }
    }

    /// <summary>
    /// Adds the fan's top artists that are not tracked yet, best rank first, at most
    /// <see cref="SpotifyOptions.AutoTrackTopArtists"/> per sync: the same <c>track_artist()</c> path as
    /// <c>POST /artists</c> (the fan is <c>added_by</c>, the daily add limit applies, onboarding is queued in the same
    /// transaction), so the rule that only tracked artists are synced still holds. Returns how many were added.
    /// Nothing happens without the client secret, because the artists could never load a catalog and activate.
    /// </summary>
    private async Task<int> TrackTopArtistsAsync(
        NpgsqlConnection connection, Guid userId, IReadOnlyList<SpotifyArtist> topArtists, CancellationToken cancellationToken)
    {
        var limit = options.Value.AutoTrackTopArtists;
        if (limit <= 0 || !options.Value.IsCatalogEnabled || topArtists.Count == 0)
        {
            return 0;
        }

        var known = (await connection.QueryAsync<string>(
                "SELECT spotify_artist_id FROM artists WHERE spotify_artist_id = ANY(@ids)",
                new { ids = topArtists.Select(a => a.Id).ToArray() }))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var artist in topArtists.Where(a => !known.Contains(a.Id)).DistinctBy(a => a.Id))
        {
            if (added >= limit)
            {
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var (artistId, created) = await connection.QuerySingleAsync<(Guid ArtistId, bool Created)>(
                    "SELECT o_artist_id, o_created FROM track_artist(@userId, @spotifyArtistId, @name)",
                    new { userId, spotifyArtistId = artist.Id, name = artist.Name }, transaction);
                if (created)
                {
                    // A separate statement: the row the function inserted is not visible to the same statement.
                    await connection.ExecuteAsync("UPDATE artists SET image_url = @imageUrl WHERE id = @artistId",
                        new { imageUrl = artist.ImageUrl, artistId }, transaction);
                    added++;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText == "daily artist add limit reached")
            {
                // The rest wait for a later sync; the fan's own adds share the limit.
                LogAddLimit(logger, userId, added);
                break;
            }
        }

        return added;
    }

    /// <summary>Replaces the user's signals. Returns false when the connection changed or was removed meanwhile.</summary>
    private static async Task<bool> SaveAsync(
        NpgsqlConnection connection, Guid userId, string spotifyUserId, SyncedSignals signals, int topArtistsAdded,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Disconnecting deletes this row first, so holding its lock keeps a disconnect from interleaving.
        var current = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM spotify_connections WHERE user_id = @userId AND spotify_user_id = @spotifyUserId FOR UPDATE)",
            new { userId, spotifyUserId }, transaction);
        if (!current)
        {
            return false;
        }

        var artists = signals.Artists;
        await connection.ExecuteAsync(
            """
            -- Rescore the artists the fan had signals for and the ones they have now (percentiles depend on all fans).
            SELECT request_artist_rescore(artist_id) FROM user_artist_signals WHERE user_id = @userId;
            DELETE FROM user_artist_signals WHERE user_id = @userId;
            INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums,
                                             top_artist_rank, recent_plays)
            SELECT @userId, * FROM unnest(@artistIds, @liked, @playlist, @albums, @ranks, @recent);
            SELECT request_artist_rescore(a) FROM unnest(@artistIds) a;

            DELETE FROM user_track_likes WHERE user_id = @userId;
            INSERT INTO user_track_likes (user_id, track_id, source, saved_at)
            SELECT @userId, t.id, x.source, x.saved_at
            FROM unnest(@trackIds, @sources, @savedAt) AS x (spotify_track_id, source, saved_at)
            JOIN tracks t ON t.spotify_track_id = x.spotify_track_id
            ON CONFLICT DO NOTHING;

            -- A sync requested while this one ran stays queued.
            UPDATE spotify_connections
            SET last_synced_at = now(), tracked_artists_found = @found, top_artists_added = @topArtistsAdded, sync_error = NULL,
                sync_state = CASE WHEN sync_state = 'running' THEN 'idle' ELSE sync_state END
            WHERE user_id = @userId;
            """,
            new
            {
                userId,
                artistIds = artists.Select(a => a.ArtistId).ToArray(),
                liked = artists.Select(a => a.LikedTracks).ToArray(),
                playlist = artists.Select(a => a.PlaylistTracks).ToArray(),
                albums = artists.Select(a => a.DistinctAlbums).ToArray(),
                ranks = artists.Select(a => a.TopArtistRank).ToArray(),
                recent = artists.Select(a => a.RecentPlays).ToArray(),
                trackIds = signals.Tracks.Select(t => t.SpotifyTrackId).ToArray(),
                sources = signals.Tracks.Select(t => t.Source).ToArray(),
                savedAt = signals.Tracks.Select(t => t.SavedAt).ToArray(),
                found = artists.Count,
                topArtistsAdded,
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task SetStateAsync(NpgsqlConnection connection, Guid userId, string state, string? error) =>
        await connection.ExecuteAsync(
            "UPDATE spotify_connections SET sync_state = @state, sync_error = @error WHERE user_id = @userId AND sync_state = 'running'",
            new { userId, state, error });

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced Spotify for user {UserId}: {Artists} tracked artists found, {Added} top artists added.")]
    private static partial void LogSynced(ILogger logger, Guid userId, int artists, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} reached the daily artist add limit after {Added} top artists; the rest wait for a later sync.")]
    private static partial void LogAddLimit(ILogger logger, Guid userId, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spotify no longer accepts user {UserId}'s connection; they must reconnect.")]
    private static partial void LogReauthRequired(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not decrypt user {UserId}'s Spotify token; they must reconnect. Check Spotify:TokenKey and Spotify:PreviousTokenKeys.")]
    private static partial void LogUnreadableToken(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spotify refused access for user {UserId} (403). In development mode, add their Spotify account to the app's users in the Spotify dashboard.")]
    private static partial void LogForbidden(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Spotify sync is not configured (Spotify:ClientId); sync marked failed.")]
    private static partial void LogNotConfigured(ILogger logger);
}
