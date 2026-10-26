using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.SpotifySync;

/// <summary>
/// openapi <c>/me/spotify</c>: connect, status, disconnect, and manual sync. Syncs run in the worker
/// (workers/spotify-sync); these endpoints only queue them. See docs/adr/0005-spotify-integration.md.
/// </summary>
public static partial class SpotifyEndpoints
{
    public static void MapSpotify(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/spotify", GetAsync);
        v1.MapPut("/me/spotify", ConnectAsync);
        v1.MapDelete("/me/spotify", DisconnectAsync);
        v1.MapPost("/me/spotify/sync", SyncAsync);
    }

    public sealed record SpotifyConnectRequest(string? Code, string? CodeVerifier, string? RedirectUri);

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await SpotifyConnections.LoadStatusAsync(connection, user.UserId()));
    }

    // The app runs Spotify's authorization with PKCE; the server exchanges the code, so the refresh token
    // never stays on the device.
    private static async Task<IResult> ConnectAsync(
        ClaimsPrincipal user, SpotifyConnectRequest request, NpgsqlDataSource db, SpotifyClient spotify,
        SpotifyTokenProtector protector, IOptions<SpotifyOptions> options, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsEnabled)
        {
            throw SpotifyConnections.Unavailable("Spotify is not set up on this server.");
        }

        var errors = new List<FieldError>();
        if (string.IsNullOrEmpty(request.Code))
        {
            errors.Add(new FieldError("code", "Send the authorization code from Spotify."));
        }

        // RFC 7636: 43 to 128 characters.
        if (request.CodeVerifier is not { Length: >= 43 and <= 128 })
        {
            errors.Add(new FieldError("code_verifier", "Send the PKCE code verifier (43 to 128 characters)."));
        }

        if (request.RedirectUri is null || !settings.RedirectUriList.Contains(request.RedirectUri, StringComparer.Ordinal))
        {
            errors.Add(new FieldError("redirect_uri", "Use one of the app's registered redirect URIs."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var logger = loggers.CreateLogger(typeof(SpotifyEndpoints));
        SpotifyTokens tokens;
        SpotifyUser account;
        try
        {
            tokens = await spotify.ExchangeCodeAsync(request.Code!, request.CodeVerifier!, request.RedirectUri!, cancellationToken);
            account = await spotify.GetMeAsync(tokens.AccessToken, cancellationToken);
        }
        catch (SpotifyAuthorizationException)
        {
            throw ApiProblemException.Validation(new FieldError("code",
                "Spotify did not accept this authorization code. It may have expired or been used; connect again."));
        }
        catch (SpotifyRequestException e) when (e.IsForbidden)
        {
            // In development mode, only Spotify accounts on the app's allowlist may use it.
            throw ApiProblemException.Validation(new FieldError("code",
                "Spotify refused access for this Spotify account. While the app is in development, only invited Spotify accounts can connect."));
        }
        catch (SpotifyRequestException e)
        {
            LogExchangeRejected(logger, e.Status, e.Error);
            throw ApiProblemException.Validation(new FieldError("code",
                "Spotify did not accept this authorization. Connect again."));
        }
        catch (SpotifyUnavailableException e)
        {
            throw SpotifyConnections.Unavailable("Spotify did not respond. Try again shortly.", e.RetryAfter);
        }

        var missing = SpotifyOptions.RequiredScopes.Except(tokens.Scopes, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw ApiProblemException.Validation(new FieldError("code",
                $"Spotify did not grant every permission the app needs (missing: {string.Join(", ", missing)}). Update the app and connect again."));
        }

        if (tokens.RefreshToken is null)
        {
            throw SpotifyConnections.Unavailable("Spotify did not return a refresh token. Try again.");
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previous = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT spotify_user_id FROM spotify_connections WHERE user_id = @userId FOR UPDATE", new { userId }, transaction);
        if (previous is not null && previous != account.Id)
        {
            // A different Spotify account: data synced from the old one must not count for the new one.
            await connection.ExecuteAsync(
                """
                DELETE FROM spotify_connections WHERE user_id = @userId;
                DELETE FROM user_track_likes WHERE user_id = @userId;
                DELETE FROM user_artist_signals WHERE user_id = @userId;
                """,
                new { userId }, transaction);
        }

        try
        {
            // Reconnecting the same account keeps connected_at and the synced data.
            await connection.ExecuteAsync(
                """
                INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes, sync_requested_at)
                VALUES (@userId, @spotifyUserId, @token, @scopes, now())
                ON CONFLICT (user_id) DO UPDATE SET refresh_token_enc = EXCLUDED.refresh_token_enc,
                                                    scopes = EXCLUDED.scopes, sync_requested_at = now()
                """,
                new
                {
                    userId,
                    spotifyUserId = account.Id,
                    token = protector.Protect(userId, tokens.RefreshToken),
                    scopes = tokens.Scopes.ToArray(),
                },
                transaction);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                          && e.ConstraintName == "spotify_connections_spotify_user_id_key")
        {
            throw SpotifyConnections.InUse();
        }

        await connection.ExecuteAsync("SELECT request_spotify_sync(@userId)", new { userId }, transaction);
        var status = await SpotifyConnections.LoadStatusAsync(connection, userId, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(status);
    }

    // Spotify has no API to revoke an app's access; the user can remove it in their Spotify account settings.
    private static async Task<IResult> DisconnectAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The connection row first: a sync finishing now locks it before writing, so it either completes
        // before this or finds the connection gone and writes nothing.
        await connection.ExecuteAsync(
            """
            DELETE FROM spotify_connections WHERE user_id = @userId;
            DELETE FROM user_track_likes WHERE user_id = @userId;
            DELETE FROM user_artist_signals WHERE user_id = @userId;
            """,
            new { userId = user.UserId() }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private sealed record SyncRequestState(string SyncState, double? WaitSeconds, bool? SyncedRecently);

    // A sync already queued or running is returned as is. Otherwise one manual sync per ManualSyncInterval.
    // background=true is the app's sync on opening (decided 2026-10-25): it never errors for timing or a lapsed
    // authorization; when a sync ran or was asked for in the last ManualSyncInterval it just returns the status (200).
    private static async Task<IResult> SyncAsync(
        ClaimsPrincipal user, bool? background, NpgsqlDataSource db, IOptions<SpotifyOptions> options, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var state = await connection.QuerySingleOrDefaultAsync<SyncRequestState>(
            """
            SELECT sync_state AS SyncState,
                   extract(epoch FROM sync_requested_at + @interval - now())::float8 AS WaitSeconds,
                   last_synced_at > now() - @interval AS SyncedRecently
            FROM spotify_connections WHERE user_id = @userId FOR UPDATE
            """,
            new { userId, interval = options.Value.ManualSyncInterval }, transaction)
            ?? throw SpotifyConnections.NotConnected();

        var quiet = background == true;
        if (quiet && (state.SyncState == "reauth_required" || state.WaitSeconds > 0 || state.SyncedRecently == true))
        {
            return Results.Ok(await SpotifyConnections.LoadStatusAsync(connection, userId, transaction));
        }

        switch (state.SyncState)
        {
            case "reauth_required":
                throw SpotifyConnections.ReauthRequired();
            case "queued" or "running":
                return Results.Accepted("/v1/me/spotify", await SpotifyConnections.LoadStatusAsync(connection, userId, transaction));
        }

        if (state.WaitSeconds is > 0 and var wait)
        {
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many sync requests",
                $"Spotify can be synced once every {options.Value.ManualSyncInterval.TotalMinutes:0} minutes.")
            {
                RetryAfter = TimeSpan.FromSeconds(wait),
            };
        }

        await connection.ExecuteAsync(
            """
            UPDATE spotify_connections SET sync_requested_at = now() WHERE user_id = @userId;
            SELECT request_spotify_sync(@userId);
            """,
            new { userId }, transaction);
        var status = await SpotifyConnections.LoadStatusAsync(connection, userId, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Accepted("/v1/me/spotify", status);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spotify rejected a code exchange with status {Status} ({Error}). Check Spotify:ClientId and the redirect URIs registered in the Spotify dashboard.")]
    private static partial void LogExchangeRejected(ILogger logger, int status, string? error);
}
