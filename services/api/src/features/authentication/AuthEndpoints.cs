using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Users;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Authentication;

/// <summary>openapi tag Auth: sign in with Apple or Google, rotate tokens, sign out.</summary>
public static class AuthEndpoints
{
    public sealed record SessionRequest(string? Provider, string? IdToken, string? Nonce, string? Handle);

    public sealed record RefreshRequest(string? RefreshToken);

    public sealed record SessionResponse(
        string AccessToken, string RefreshToken, int ExpiresIn, string TokenType, bool IsNewUser, MeResponse User);

    public static void MapAuth(this RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth");
        auth.MapPost("/sessions", SignInAsync).AllowAnonymous().RequireRateLimiting(AuthSetup.SignInRateLimit);
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(AuthSetup.SignInRateLimit);
        auth.MapDelete("/sessions/current", SignOutAsync);
    }

    private static async Task<IResult> SignInAsync(
        SessionRequest request, IIdentityTokenValidator validator, SessionService sessions, NpgsqlDataSource db,
        CancellationToken cancellationToken)
    {
        if (request.Provider is not ("apple" or "google"))
        {
            throw ApiProblemException.Validation(new FieldError("provider", "Use apple or google."));
        }

        if (string.IsNullOrEmpty(request.IdToken))
        {
            throw ApiProblemException.Validation(new FieldError("id_token", "Required."));
        }

        if (!validator.IsEnabled(request.Provider))
        {
            throw ApiProblemException.Validation(new FieldError("provider", "This sign-in provider is not enabled."));
        }

        // The nonce ties the identity token to this sign-in attempt, so a token lifted from elsewhere (another site
        // using the same Google client, a log) can't be replayed here. Every client sends one; without it, or when
        // the token doesn't carry it, the answer is the same 401 as for any other bad token.
        if (string.IsNullOrEmpty(request.Nonce))
        {
            throw ApiProblemException.Unauthorized("The identity token is invalid or expired.");
        }

        var identity = await validator.ValidateAsync(request.Provider, request.IdToken, request.Nonce, cancellationToken)
            ?? throw ApiProblemException.Unauthorized("The identity token is invalid or expired.");

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var userId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            UPDATE auth_identities SET last_used_at = now()
            WHERE provider = @Provider::auth_provider AND subject = @Subject
            RETURNING user_id
            """,
            new { request.Provider, identity.Subject }, transaction);

        var isNewUser = userId is null;
        userId ??= await CreateAccountAsync(connection, transaction, request, identity);

        var tokens = await sessions.StartAsync(connection, transaction, userId.Value);
        var me = await UserProfiles.LoadMeAsync(connection, userId.Value, transaction)
            ?? throw new InvalidOperationException("Signed-in user has no profile.");
        await transaction.CommitAsync(cancellationToken);

        return Results.Json(
            new SessionResponse(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn, "Bearer", isNewUser, me),
            statusCode: isNewUser ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<Guid> CreateAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionRequest request, ProviderIdentity identity)
    {
        if (request.Handle is null || !UserProfiles.HandlePattern().IsMatch(request.Handle))
        {
            throw ApiProblemException.Validation(new FieldError(
                "handle", request.Handle is null ? "Required to create an account." : UserProfiles.HandleRule));
        }

        if (string.IsNullOrEmpty(identity.Email) || !identity.EmailVerified)
        {
            throw new ApiProblemException(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                "Verified email required", "The sign-in provider did not share a verified email address.");
        }

        try
        {
            var userId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO users (handle, email) VALUES (@Handle, @Email) RETURNING id",
                new { request.Handle, identity.Email }, transaction);
            await connection.ExecuteAsync(
                "INSERT INTO auth_identities (provider, subject, user_id) VALUES (@Provider::auth_provider, @Subject, @userId)",
                new { request.Provider, identity.Subject, userId }, transaction);
            return userId;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw e.ConstraintName switch
            {
                "users_handle_key" => UserProfiles.HandleTaken(),
                // One email per account: a second provider is not linked to an existing account (ADR 0003).
                "users_email_key" => new ApiProblemException(StatusCodes.Status409Conflict, "email_in_use",
                    "Email already in use", "Another account uses this email. Sign in with the provider you used before."),
                // Two first sign-ins with the same provider account raced; the other one won.
                _ => new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Sign-in conflict", "Try again."),
            };
        }
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request, SessionService sessions, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            throw ApiProblemException.Unauthorized("A refresh token is required.");
        }

        var (userId, tokens) = await sessions.RefreshAsync(request.RefreshToken, cancellationToken);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var me = await UserProfiles.LoadMeAsync(connection, userId)
            ?? throw ApiProblemException.Unauthorized("The account no longer exists.");

        return Results.Ok(new SessionResponse(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn, "Bearer", false, me));
    }

    private static async Task<IResult> SignOutAsync(ClaimsPrincipal user, SessionService sessions, CancellationToken cancellationToken)
    {
        await sessions.SignOutAsync(user.SessionId(), cancellationToken);
        return Results.NoContent();
    }
}
