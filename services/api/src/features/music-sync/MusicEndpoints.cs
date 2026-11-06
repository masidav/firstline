using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using FandomScore.MusicProviders;
using FandomScore.Spotify;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.MusicSync;

/// <summary>openapi MusicStatus: one provider's connection and sync state (migration 0058).</summary>
public sealed record MusicStatus(
    string Provider, bool Connected, DateTime? ConnectedAt, DateTime? LastSyncedAt, string SyncState, string? SyncError,
    int TrackedArtistsFound)
{
    public static MusicStatus NotConnected(string provider) => new(provider, false, null, null, "idle", null, 0);
}

/// <summary>
/// openapi <c>/me/music/{provider}</c>: connect, status, disconnect, and manual sync for Apple Music and Tidal
/// (ADR 0007). Apple Music: the app's MusicKit hands over a Music User Token, checked here with one call; Tidal: the
/// app runs the authorization with PKCE and sends the code, exchanged here. Tokens are stored encrypted with the
/// Spotify token key under a purpose per provider; syncs run in the worker (workers/music-sync).
/// </summary>
public static partial class MusicEndpoints
{
    public static void MapMusic(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/music", ListAsync);
        v1.MapGet("/me/music/apple_music/developer-token", DeveloperTokenAsync);
        v1.MapGet("/me/music/{provider}", GetAsync);
        v1.MapPut("/me/music/{provider}", ConnectAsync);
        v1.MapDelete("/me/music/{provider}", DisconnectAsync);
        v1.MapPost("/me/music/{provider}/sync", SyncAsync);
    }

    public sealed record MusicConnectRequest(string? MusicUserToken, string? Code, string? CodeVerifier, string? RedirectUri);

    public sealed record MusicStatusList(IReadOnlyList<MusicStatus> Items);

    public sealed record DeveloperToken(string Token, int ExpiresIn);

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var statuses = new List<MusicStatus>();
        foreach (var provider in MusicProvider.All)
        {
            statuses.Add(await LoadStatusAsync(connection, user.UserId(), provider));
        }

        return Results.Ok(new MusicStatusList(statuses));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, string provider, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        RequireKnown(provider);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadStatusAsync(connection, user.UserId(), provider));
    }

    // The app's MusicKit needs a developer token to ask the fan for a Music User Token. Short-lived and tied to our
    // team, it only lets a client talk to Apple's catalog on our behalf.
    private static async Task<IResult> DeveloperTokenAsync(
        AppleMusicClient appleMusic, IOptions<MusicProviderOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.AppleMusic.IsEnabled)
        {
            throw Unavailable(MusicProvider.AppleMusic, "Apple Music is not set up on this server.");
        }

        var token = await appleMusic.DeveloperTokenAsync(cancellationToken);
        return Results.Ok(new DeveloperToken(token, (int)options.Value.AppleMusic.DeveloperTokenLifetime.TotalSeconds));
    }

    private static async Task<IResult> ConnectAsync(
        ClaimsPrincipal user, string provider, MusicConnectRequest request, NpgsqlDataSource db, AppleMusicClient appleMusic,
        TidalClient tidal, SpotifyTokenProtector protector, IOptions<MusicProviderOptions> options, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        RequireKnown(provider);
        var settings = options.Value;
        if (!settings.IsEnabled(provider))
        {
            throw Unavailable(provider, $"{Name(provider)} is not set up on this server.");
        }

        var logger = loggers.CreateLogger(typeof(MusicEndpoints));
        string storedToken;
        string? providerUserId = null;
        string? storefront = null;
        string[] scopes = [];
        try
        {
            if (provider == MusicProvider.AppleMusic)
            {
                if (string.IsNullOrEmpty(request.MusicUserToken) || request.MusicUserToken.Length > 4096)
                {
                    throw ApiProblemException.Validation(new FieldError("music_user_token", "Send the Music User Token from MusicKit."));
                }

                storefront = (await appleMusic.GetMeAsync(request.MusicUserToken, cancellationToken)).Storefront;
                storedToken = request.MusicUserToken;
            }
            else
            {
                var errors = new List<FieldError>();
                if (string.IsNullOrEmpty(request.Code))
                {
                    errors.Add(new FieldError("code", "Send the authorization code from Tidal."));
                }

                if (request.CodeVerifier is not { Length: >= 43 and <= 128 })
                {
                    errors.Add(new FieldError("code_verifier", "Send the PKCE code verifier (43 to 128 characters)."));
                }

                if (request.RedirectUri is null || !settings.Tidal.RedirectUriList.Contains(request.RedirectUri, StringComparer.Ordinal))
                {
                    errors.Add(new FieldError("redirect_uri", "Use one of the app's registered redirect URIs."));
                }

                if (errors.Count > 0)
                {
                    throw ApiProblemException.Validation([.. errors]);
                }

                var tokens = await tidal.ExchangeCodeAsync(request.Code!, request.CodeVerifier!, request.RedirectUri!, cancellationToken);
                var missing = TidalOptions.RequiredScopes.Except(tokens.Scopes, StringComparer.Ordinal).ToArray();
                if (missing.Length > 0)
                {
                    throw ApiProblemException.Validation(new FieldError("code",
                        $"Tidal did not grant every permission the app needs (missing: {string.Join(", ", missing)}). Update the app and connect again."));
                }

                if (tokens.RefreshToken is null)
                {
                    throw Unavailable(provider, "Tidal did not return a refresh token. Try again.");
                }

                providerUserId = (await tidal.GetMeAsync(tokens.AccessToken, cancellationToken)).Id;
                storedToken = tokens.RefreshToken;
                scopes = [.. tokens.Scopes];
            }
        }
        catch (MusicAuthorizationException)
        {
            throw ApiProblemException.Validation(new FieldError(provider == MusicProvider.AppleMusic ? "music_user_token" : "code",
                $"{Name(provider)} did not accept this authorization. It may have expired; connect again."));
        }
        catch (MusicRequestException e)
        {
            LogConnectRejected(logger, provider, e.Status, e.Error);
            throw ApiProblemException.Validation(new FieldError(provider == MusicProvider.AppleMusic ? "music_user_token" : "code",
                $"{Name(provider)} did not accept this authorization. Connect again."));
        }
        catch (MusicUnavailableException e)
        {
            throw Unavailable(provider, $"{Name(provider)} did not respond. Try again shortly.", e.RetryAfter);
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previous = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT provider_user_id FROM music_connections WHERE user_id = @userId AND provider = @provider FOR UPDATE",
            new { userId, provider }, transaction);
        if (previous is not null && previous != providerUserId)
        {
            // A different account on the same service: its data must not count for the new one.
            await DeleteProviderDataAsync(connection, transaction, userId, provider);
        }

        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO music_connections (user_id, provider, provider_user_id, token_enc, scopes, storefront, sync_requested_at)
                VALUES (@userId, @provider, @providerUserId, @token, @scopes, @storefront, now())
                ON CONFLICT (user_id, provider) DO UPDATE SET provider_user_id = EXCLUDED.provider_user_id, token_enc = EXCLUDED.token_enc,
                    scopes = EXCLUDED.scopes, storefront = EXCLUDED.storefront, sync_requested_at = now(),
                    sync_state = CASE WHEN music_connections.sync_state = 'reauth_required' THEN 'idle' ELSE music_connections.sync_state END,
                    sync_error = NULL
                """,
                new
                {
                    userId,
                    provider,
                    providerUserId,
                    token = protector.Protect(userId, storedToken, MusicProvider.TokenPurpose(provider)),
                    scopes,
                    storefront,
                },
                transaction);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "music_connections_provider_user_key")
        {
            throw InUse(provider);
        }

        await connection.ExecuteAsync("SELECT request_music_sync(@userId, @provider)", new { userId, provider }, transaction);
        var status = await LoadStatusAsync(connection, userId, provider, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(status);
    }

    private static async Task<IResult> DisconnectAsync(ClaimsPrincipal user, string provider, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        RequireKnown(provider);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DeleteProviderDataAsync(connection, transaction, user.UserId(), provider);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    // The connection row first: a sync finishing now locks it before writing, so it either completes before this or
    // finds the connection gone and writes nothing. Only this provider's rows go; Spotify's and the other's stay.
    private static async Task DeleteProviderDataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string provider) =>
        await connection.ExecuteAsync(
            """
            DELETE FROM music_connections WHERE user_id = @userId AND provider = @provider;
            DELETE FROM user_track_likes WHERE user_id = @userId AND provider = @provider;
            DELETE FROM user_artist_signals WHERE user_id = @userId AND provider = @provider;
            DELETE FROM user_album_saves WHERE user_id = @userId AND provider = @provider;
            DELETE FROM user_plays WHERE user_id = @userId AND provider = @provider;
            """,
            new { userId, provider }, transaction);

    private sealed record SyncRequestState(string SyncState, double? WaitSeconds);

    private static async Task<IResult> SyncAsync(
        ClaimsPrincipal user, string provider, NpgsqlDataSource db, IOptions<MusicProviderOptions> options, CancellationToken cancellationToken)
    {
        RequireKnown(provider);
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var state = await connection.QuerySingleOrDefaultAsync<SyncRequestState>(
            """
            SELECT sync_state AS SyncState, extract(epoch FROM sync_requested_at + @interval - now())::float8 AS WaitSeconds
            FROM music_connections WHERE user_id = @userId AND provider = @provider FOR UPDATE
            """,
            new { userId, provider, interval = options.Value.ManualSyncInterval }, transaction)
            ?? throw NotConnected(provider);

        switch (state.SyncState)
        {
            case "reauth_required":
                throw ReauthRequired(provider);
            case "queued" or "running":
                return Results.Accepted($"/v1/me/music/{provider}", await LoadStatusAsync(connection, userId, provider, transaction));
        }

        if (state.WaitSeconds is > 0 and var wait)
        {
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many sync requests",
                $"{Name(provider)} can be synced once every {options.Value.ManualSyncInterval.TotalMinutes:0} minutes.")
            {
                RetryAfter = TimeSpan.FromSeconds(wait),
            };
        }

        await connection.ExecuteAsync(
            """
            UPDATE music_connections SET sync_requested_at = now() WHERE user_id = @userId AND provider = @provider;
            SELECT request_music_sync(@userId, @provider);
            """,
            new { userId, provider }, transaction);
        var status = await LoadStatusAsync(connection, userId, provider, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Accepted($"/v1/me/music/{provider}", status);
    }

    private sealed record StatusRow(DateTime ConnectedAt, DateTime? LastSyncedAt, string SyncState, string? SyncError, int TrackedArtistsFound);

    public static async Task<MusicStatus> LoadStatusAsync(
        NpgsqlConnection connection, Guid userId, string provider, NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StatusRow>(
            """
            SELECT connected_at AS ConnectedAt, last_synced_at AS LastSyncedAt, sync_state AS SyncState, sync_error AS SyncError,
                   tracked_artists_found AS TrackedArtistsFound
            FROM music_connections WHERE user_id = @userId AND provider = @provider
            """,
            new { userId, provider }, transaction);
        return row is null
            ? MusicStatus.NotConnected(provider)
            : new MusicStatus(provider, true, row.ConnectedAt, row.LastSyncedAt, row.SyncState, row.SyncError, row.TrackedArtistsFound);
    }

    private static void RequireKnown(string provider)
    {
        if (!MusicProvider.IsKnown(provider))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Unknown provider", "Use apple_music or tidal.");
        }
    }

    private static string Name(string provider) => provider == MusicProvider.AppleMusic ? "Apple Music" : "Tidal";

    private static ApiProblemException NotConnected(string provider) =>
        new(StatusCodes.Status409Conflict, "music_not_connected", $"{Name(provider)} is not connected", $"Connect {Name(provider)} first.");

    private static ApiProblemException ReauthRequired(string provider) =>
        new(StatusCodes.Status409Conflict, "music_reauth_required", $"{Name(provider)} needs to be connected again",
            $"{Name(provider)} no longer accepts this connection. Connect it again.");

    private static ApiProblemException InUse(string provider) =>
        new(StatusCodes.Status409Conflict, "music_in_use", $"{Name(provider)} account already connected",
            $"This {Name(provider)} account is connected to another account here.");

    private static ApiProblemException Unavailable(string provider, string detail, TimeSpan? retryAfter = null) =>
        new(StatusCodes.Status503ServiceUnavailable, "music_unavailable", $"{Name(provider)} is unavailable", detail) { RetryAfter = retryAfter };

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} rejected a connection with status {Status} ({Error}). Check the MusicProviders configuration and the redirect URIs registered with the provider.")]
    private static partial void LogConnectRejected(ILogger logger, string provider, int status, string? error);
}
