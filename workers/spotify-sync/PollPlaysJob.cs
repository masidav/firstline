using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.SpotifySync;

/// <summary>
/// Extends one fan's play ledger (<c>user_plays</c>) from Spotify's recently-played endpoint (payload
/// <c>{"user_id": ...}</c>, queued hourly by <see cref="ScheduleSpotifySyncsJob"/>). Spotify keeps only the last
/// 50 plays, so this runs often; it reads plays after the stored cursor, keeps those of tracked active artists,
/// and prunes plays older than <see cref="SpotifyOptions.PlayHistoryRetention"/>. Idempotent: plays are keyed by
/// time and track. Shares the sync's per-user lock, because both refresh the token and Spotify may rotate it.
/// </summary>
public sealed partial class PollPlaysJob(
    NpgsqlDataSource db, SpotifyClient spotify, SpotifyTokenProtector protector, IOptions<SpotifyOptions> options,
    ILogger<PollPlaysJob> logger) : IJobHandler
{
    public const string JobKind = "spotify.poll_plays";

    public string Kind => JobKind;

    private sealed record Connection(string SpotifyUserId, byte[] RefreshTokenEnc, long? PlaysCursor);

    /// <summary>Spotify's <c>after</c> cursor for a set of plays: the newest play in milliseconds since the epoch.</summary>
    public static long? Cursor(IReadOnlyList<PlaySignal> plays) =>
        plays.Count == 0 ? null : new DateTimeOffset(plays.Max(p => p.PlayedAt), TimeSpan.Zero).ToUnixTimeMilliseconds();

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var payload = JsonDocument.Parse(job.Payload);
        var userId = payload.RootElement.GetProperty("user_id").GetGuid();
        if (!options.Value.IsEnabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var lockKey = $"spotify.sync:{userId}";
        if (!await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@lockKey, 0))", new { lockKey }))
        {
            throw new InvalidOperationException("A Spotify sync or poll for this user is running.");
        }

        try
        {
            await PollAsync(connection, userId, cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@lockKey, 0))", new { lockKey });
        }
    }

    private async Task PollAsync(NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        var start = await connection.QuerySingleOrDefaultAsync<Connection>(
            """
            UPDATE spotify_connections SET plays_polled_at = now()
            WHERE user_id = @userId AND sync_state <> 'reauth_required'
            RETURNING spotify_user_id AS SpotifyUserId, refresh_token_enc AS RefreshTokenEnc, plays_cursor AS PlaysCursor
            """,
            new { userId });
        if (start is null)
        {
            return;
        }

        try
        {
            var refreshToken = protector.Unprotect(userId, start.RefreshTokenEnc);
            var tokens = await spotify.RefreshAsync(refreshToken, cancellationToken);
            await connection.ExecuteAsync(
                "UPDATE spotify_connections SET refresh_token_enc = @token WHERE user_id = @userId AND spotify_user_id = @spotifyUserId",
                new { userId, start.SpotifyUserId, token = protector.Protect(userId, tokens.RefreshToken ?? refreshToken) });

            var recent = await spotify.GetRecentlyPlayedAsync(tokens.AccessToken, start.PlaysCursor, cancellationToken);
            var tracked = (await connection.QueryAsync<(string SpotifyArtistId, Guid Id)>(
                    "SELECT spotify_artist_id, id FROM artists WHERE status = 'active'"))
                .ToDictionary(a => a.SpotifyArtistId, a => a.Id);
            var plays = SpotifySignals.ReducePlays(recent, tracked);
            // Plays of untracked artists still move the cursor, so they are never fetched again.
            var cursor = recent.Count == 0 ? null : (long?)recent.Max(p => p.PlayedAt).ToUnixTimeMilliseconds();

            var touched = await connection.ExecuteScalarAsync<int>(
                """
                WITH inserted AS (
                  INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at)
                  SELECT @userId, * FROM unnest(@artistIds, @trackIds, @playedAt)
                  ON CONFLICT DO NOTHING
                  RETURNING artist_id
                ), pruned AS (
                  DELETE FROM user_plays WHERE user_id = @userId AND played_at < now() - @retention
                ), moved AS (
                  UPDATE spotify_connections SET plays_cursor = greatest(plays_cursor, @cursor)
                  WHERE user_id = @userId AND spotify_user_id = @spotifyUserId
                )
                SELECT count(*)::integer FROM inserted;
                -- New plays move the fan's score (percentiles depend on every fan).
                SELECT request_artist_rescore(a) FROM unnest(@rescoreArtistIds) a;
                """,
                new
                {
                    userId,
                    start.SpotifyUserId,
                    artistIds = plays.Select(p => p.ArtistId).ToArray(),
                    trackIds = plays.Select(p => p.SpotifyTrackId).ToArray(),
                    playedAt = plays.Select(p => p.PlayedAt).ToArray(),
                    cursor,
                    retention = options.Value.PlayHistoryRetention,
                    rescoreArtistIds = plays.Select(p => p.ArtistId).Distinct().ToArray(),
                });
            LogPolled(logger, userId, recent.Count, touched);
        }
        catch (SpotifyAuthorizationException)
        {
            // The next sync marks the connection; polls stop meanwhile.
            LogReauthRequired(logger, userId);
            await connection.ExecuteAsync(
                "UPDATE spotify_connections SET sync_state = 'reauth_required', sync_error = 'spotify_reauth_required' WHERE user_id = @userId AND sync_state IN ('idle', 'failed')",
                new { userId });
        }
        catch (CryptographicException e)
        {
            LogUnreadableToken(logger, e, userId);
        }
        catch (SpotifyRequestException e) when (e.IsForbidden)
        {
            // Development mode: account not allowlisted. The next poll tries again.
            LogForbidden(logger, userId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Polled Spotify plays for user {UserId}: {Fetched} new plays, {Stored} of tracked artists stored.")]
    private static partial void LogPolled(ILogger logger, Guid userId, int fetched, int stored);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spotify no longer accepts user {UserId}'s connection (poll); they must reconnect.")]
    private static partial void LogReauthRequired(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not decrypt user {UserId}'s Spotify token (poll). Check Spotify:TokenKey and Spotify:PreviousTokenKeys.")]
    private static partial void LogUnreadableToken(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spotify refused access for user {UserId} (403) during the play poll.")]
    private static partial void LogForbidden(ILogger logger, Guid userId);
}
