using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FandomScore.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FandomScore.Api.Features.Authentication;

public sealed record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Sessions, rotating refresh tokens, and access tokens (docs/adr/0003-authentication.md).
/// Refresh tokens are random and stored only as SHA-256; access tokens are short-lived JWTs naming the session.
/// </summary>
public sealed class SessionService(NpgsqlDataSource db, IOptions<AuthOptions> options, TimeProvider time)
{
    private static readonly JsonWebTokenHandler Handler = new();
    private readonly AuthOptions _options = options.Value;

    public static SymmetricSecurityKey SigningKey(AuthOptions options) => new(Convert.FromBase64String(options.SigningKey));

    /// <summary>Starts a session for a signed-in user, inside the caller's transaction.</summary>
    public async Task<IssuedTokens> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + _options.SessionLifetime;
        var sessionId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO auth_sessions (user_id, created_at, last_refreshed_at, expires_at)
            VALUES (@userId, @now, @now, @expiresAt)
            RETURNING id
            """,
            new { userId, now, expiresAt }, transaction);

        var refreshToken = await IssueRefreshTokenAsync(connection, transaction, sessionId, now, expiresAt);
        return new IssuedTokens(IssueAccessToken(userId, sessionId, now), refreshToken, ExpiresInSeconds);
    }

    /// <summary>
    /// Exchanges a refresh token for new tokens. A token that was already used revokes its whole session,
    /// because one of the two holders is an attacker.
    /// </summary>
    public async Task<(Guid UserId, IssuedTokens Tokens)> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Row locks make concurrent uses of one token take turns: the second one sees it used.
        var row = await connection.QuerySingleOrDefaultAsync<RefreshRow>(
            """
            SELECT t.session_id AS SessionId, s.user_id AS UserId, s.expires_at AS SessionExpiresAt,
                   t.used_at IS NOT NULL AS Used,
                   (t.expires_at > @now AND s.expires_at > @now AND s.revoked_at IS NULL) AS Usable
            FROM refresh_tokens t JOIN auth_sessions s ON s.id = t.session_id
            WHERE t.token_hash = @hash
            FOR UPDATE OF t, s
            """,
            new { hash = Hash(refreshToken), now }, transaction);

        if (row is null || !row.Usable)
        {
            throw ApiProblemException.Unauthorized("The refresh token is invalid or expired. Sign in again.");
        }

        if (row.Used)
        {
            await RevokeAsync(connection, transaction, row.SessionId, "token_reuse", now);
            await transaction.CommitAsync(cancellationToken);
            throw ApiProblemException.Unauthorized("The refresh token was already used. Sign in again.");
        }

        await connection.ExecuteAsync(
            """
            UPDATE refresh_tokens SET used_at = @now WHERE token_hash = @hash;
            UPDATE auth_sessions SET last_refreshed_at = @now WHERE id = @SessionId;
            """,
            new { hash = Hash(refreshToken), now, row.SessionId }, transaction);
        var newRefreshToken = await IssueRefreshTokenAsync(
            connection, transaction, row.SessionId, now, new DateTimeOffset(row.SessionExpiresAt, TimeSpan.Zero));
        await transaction.CommitAsync(cancellationToken);

        return (row.UserId, new IssuedTokens(IssueAccessToken(row.UserId, row.SessionId, now), newRefreshToken, ExpiresInSeconds));
    }

    /// <summary>Signs a session out. Its access tokens stop working on the next request.</summary>
    public async Task SignOutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RevokeAsync(connection, null, sessionId, "sign_out", time.GetUtcNow());
    }

    /// <summary>Checked on every authenticated request, so sign-out and account deletion take effect at once.</summary>
    public async Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM auth_sessions
                           WHERE id = @sessionId AND user_id = @userId AND revoked_at IS NULL AND expires_at > @now)
            """,
            new { sessionId, userId, now = time.GetUtcNow() });
    }

    private int ExpiresInSeconds => (int)_options.AccessTokenLifetime.TotalSeconds;

    private string IssueAccessToken(Guid userId, Guid sessionId, DateTimeOffset now) =>
        Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + _options.AccessTokenLifetime).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Sid] = sessionId.ToString(),
            },
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
        });

    private async Task<string> IssueRefreshTokenAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, DateTimeOffset now, DateTimeOffset sessionExpiresAt)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + _options.RefreshTokenLifetime;
        await connection.ExecuteAsync(
            "INSERT INTO refresh_tokens (token_hash, session_id, issued_at, expires_at) VALUES (@hash, @sessionId, @now, @expiresAt)",
            new { hash = Hash(token), sessionId, now, expiresAt = expiresAt < sessionExpiresAt ? expiresAt : sessionExpiresAt },
            transaction);
        return token;
    }

    private static Task<int> RevokeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, string reason, DateTimeOffset now) =>
        connection.ExecuteAsync(
            "UPDATE auth_sessions SET revoked_at = @now, revoked_reason = @reason WHERE id = @sessionId AND revoked_at IS NULL",
            new { sessionId, reason, now }, transaction);

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public sealed record RefreshRow(Guid SessionId, Guid UserId, DateTime SessionExpiresAt, bool Used, bool Usable);
}
