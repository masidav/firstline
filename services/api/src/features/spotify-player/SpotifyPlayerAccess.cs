using System.Security.Cryptography;
using Dapper;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Spotify;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.SpotifyPlayer;

/// <summary>A fan's Spotify connection as the player needs it.</summary>
public sealed record PlayerConnection(string SpotifyUserId, string[] Scopes, string SyncState)
{
    /// <summary>Made with the playback scopes and still accepted by Spotify.</summary>
    public bool CanUsePlayer =>
        SyncState != "reauth_required"
        && Scopes.Contains(SpotifyOptions.PlaybackReadScope)
        && Scopes.Contains(SpotifyOptions.PlaybackControlScope);

    /// <summary>Made with the library scope (Like buttons, 2026-10-31) and still accepted by Spotify.</summary>
    public bool CanUseLibrary =>
        SyncState != "reauth_required" && Scopes.Contains(SpotifyOptions.LibraryModifyScope);
}

/// <summary>
/// Access tokens for the player endpoints (ADR 0005, amended 2026-10-26). A token is refreshed from the stored
/// refresh token under the same per-fan advisory lock as the sync worker (Spotify may rotate the refresh token, and
/// two refreshes racing would lose it), and kept in this process's memory until a minute before it expires. Access
/// tokens are never written to the database or logged.
/// </summary>
public sealed partial class SpotifyPlayerAccess(
    NpgsqlDataSource db, SpotifyClient spotify, SpotifyTokenProtector protector, IMemoryCache cache,
    IOptions<SpotifyOptions> options, TimeProvider time, ILogger<SpotifyPlayerAccess> logger)
{
    private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SyncingRetryAfter = TimeSpan.FromSeconds(5);

    public async Task<PlayerConnection?> LoadConnectionAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ConnectionRow>(
            "SELECT spotify_user_id AS SpotifyUserId, scopes AS Scopes, sync_state AS SyncState FROM spotify_connections WHERE user_id = @userId",
            new { userId });
        return row is null ? null : new PlayerConnection(row.SpotifyUserId, row.Scopes, row.SyncState);
    }

    // Dapper maps text[] onto properties, not record constructors.
    private sealed class ConnectionRow
    {
        public string SpotifyUserId { get; init; } = "";

        public string[] Scopes { get; init; } = [];

        public string SyncState { get; init; } = "";
    }

    /// <summary>
    /// Calls Spotify with the fan's access token. When Spotify rejects a cached token, gets a fresh one and tries
    /// once more (a 401 means the call did nothing). Throws <c>spotify_reauth_required</c> when Spotify no longer
    /// accepts the connection, and <c>spotify_unavailable</c> while a sync holds the lock.
    /// </summary>
    public async Task<T> CallAsync<T>(
        Guid userId, PlayerConnection connection, Func<string, Task<T>> call, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(userId, connection.SpotifyUserId, fresh: false, cancellationToken);
        try
        {
            return await call(token);
        }
        catch (SpotifyAuthorizationException)
        {
            cache.Remove(TokenKey(userId, connection.SpotifyUserId));
        }

        token = await GetTokenAsync(userId, connection.SpotifyUserId, fresh: true, cancellationToken);
        try
        {
            return await call(token);
        }
        catch (SpotifyAuthorizationException)
        {
            cache.Remove(TokenKey(userId, connection.SpotifyUserId));
            await MarkReauthRequiredAsync(userId, cancellationToken);
            throw SpotifyConnections.ReauthRequired();
        }
    }

    private static string TokenKey(Guid userId, string spotifyUserId) => $"spotify-access:{userId}:{spotifyUserId}";

    private async Task<string> GetTokenAsync(Guid userId, string spotifyUserId, bool fresh, CancellationToken cancellationToken)
    {
        var key = TokenKey(userId, spotifyUserId);
        if (!fresh && cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return cached;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var lockKey = $"spotify.sync:{userId}";   // the sync worker's lock
        var deadline = time.GetUtcNow() + options.Value.PlayerLockWait;
        while (!await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@lockKey, 0))", new { lockKey }))
        {
            if (time.GetUtcNow() >= deadline)
            {
                throw SpotifyConnections.Unavailable("Spotify is syncing your library. Try again in a few seconds.", SyncingRetryAfter);
            }

            await Task.Delay(LockPoll, time, cancellationToken);
        }

        try
        {
            // Another request may have refreshed while this one waited.
            if (!fresh && cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            var stored = await connection.QuerySingleOrDefaultAsync<byte[]>(
                "SELECT refresh_token_enc FROM spotify_connections WHERE user_id = @userId AND spotify_user_id = @spotifyUserId AND sync_state <> 'reauth_required'",
                new { userId, spotifyUserId }) ?? throw SpotifyConnections.NotConnected();

            SpotifyTokens tokens;
            string refreshToken;
            try
            {
                refreshToken = protector.Unprotect(userId, stored);
                tokens = await spotify.RefreshAsync(refreshToken, cancellationToken);
            }
            catch (Exception e) when (e is SpotifyAuthorizationException or CryptographicException)
            {
                LogReauthRequired(logger, userId);
                await MarkReauthRequiredAsync(connection, userId);
                throw SpotifyConnections.ReauthRequired();
            }
            catch (SpotifyUnavailableException e)
            {
                throw SpotifyConnections.Unavailable("Spotify did not respond. Try again shortly.", e.RetryAfter);
            }

            // Saved before anything else: the old refresh token may stop working (same as the sync).
            await connection.ExecuteAsync(
                "UPDATE spotify_connections SET refresh_token_enc = @token WHERE user_id = @userId AND spotify_user_id = @spotifyUserId",
                new { userId, spotifyUserId, token = protector.Protect(userId, tokens.RefreshToken ?? refreshToken) });

            var lifetime = tokens.ExpiresIn - TimeSpan.FromMinutes(1);
            cache.Set(key, tokens.AccessToken, lifetime > TimeSpan.FromMinutes(1) ? lifetime : TimeSpan.FromMinutes(1));
            return tokens.AccessToken;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@lockKey, 0))", new { lockKey });
        }
    }

    private async Task MarkReauthRequiredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await MarkReauthRequiredAsync(connection, userId);
    }

    // Like the play poll: a queued or running sync marks the connection itself.
    private static Task<int> MarkReauthRequiredAsync(NpgsqlConnection connection, Guid userId) =>
        connection.ExecuteAsync(
            "UPDATE spotify_connections SET sync_state = 'reauth_required', sync_error = 'spotify_reauth_required' WHERE user_id = @userId AND sync_state IN ('idle', 'failed')",
            new { userId });

    [LoggerMessage(Level = LogLevel.Information, Message = "Spotify no longer accepts the connection of user {UserId}; the player needs a reconnect.")]
    private static partial void LogReauthRequired(ILogger logger, Guid userId);
}
