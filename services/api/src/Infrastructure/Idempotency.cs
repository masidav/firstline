using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// <c>Idempotency-Key</c> for creating endpoints (openapi conventions): the same key with the same body replays
/// the stored response; a different body is <c>409 idempotency_conflict</c>. Keys are per user and kept 24 hours
/// (<c>idempotency_keys</c>, migration 0009).
/// </summary>
public static class Idempotency
{
    public const string Header = "Idempotency-Key";
    private const int MaxKeyLength = 64;

    public sealed record Replay(int StatusCode, string Body)
    {
        public IResult ToResult() => Results.Text(Body, "application/json", statusCode: StatusCode);
    }

    /// <summary>The request's key, or null without the header. A malformed key is a validation error.</summary>
    public static string? ReadKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue(Header, out var values) || values.Count == 0)
        {
            return null;
        }

        var key = values[0];
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            throw ApiProblemException.Validation(new FieldError(Header, $"Use 1 to {MaxKeyLength} characters."));
        }

        return key;
    }

    public static byte[] Hash(string requestBody) => SHA256.HashData(Encoding.UTF8.GetBytes(requestBody));

    /// <summary>
    /// Claims the key inside <paramref name="transaction"/>. Returns a stored response to replay, or null when
    /// this call owns the key and must do the work, then call <see cref="CompleteAsync"/> before committing.
    /// </summary>
    public static async Task<Replay?> BeginAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string key, byte[] requestHash)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Tidy this user's expired keys first, so a reused key after 24 hours starts fresh.
        var claimed = await connection.ExecuteScalarAsync<bool>(
            """
            DELETE FROM idempotency_keys WHERE user_id = @userId AND created_at < now() - interval '24 hours';
            INSERT INTO idempotency_keys (user_id, key, request_sha256) VALUES (@userId, @key, @requestHash)
            ON CONFLICT (user_id, key) DO NOTHING
            RETURNING true
            """,
            new { userId, key, requestHash }, transaction);
        if (claimed)
        {
            return null;
        }

        // Someone holds the key. If their transaction was still running, the insert above waited for it.
        var existing = await connection.QuerySingleOrDefaultAsync<(byte[] Hash, int? Status, string? Body)>(
            "SELECT request_sha256, status_code, response::text FROM idempotency_keys WHERE user_id = @userId AND key = @key",
            new { userId, key }, transaction);
        if (existing.Hash is null || !existing.Hash.AsSpan().SequenceEqual(requestHash))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "idempotency_conflict", "Idempotency key reused",
                "This Idempotency-Key was already used with a different request body.");
        }

        if (existing.Status is not { } status || existing.Body is null)
        {
            // The first call failed after claiming the key (its row stayed only if it committed, so this is rare).
            throw new ApiProblemException(StatusCodes.Status409Conflict, "idempotency_conflict", "Idempotency key in use",
                "The first request with this Idempotency-Key did not finish. Retry with a new key.");
        }

        return new Replay(status, existing.Body);
    }

    public static Task CompleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string key, int statusCode, string responseJson)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteAsync(
            "UPDATE idempotency_keys SET status_code = @statusCode, response = @responseJson::json WHERE user_id = @userId AND key = @key",
            new { userId, key, statusCode, responseJson }, transaction);
    }
}
