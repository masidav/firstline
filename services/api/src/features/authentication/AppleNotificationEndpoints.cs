using System.Text.Json;
using Dapper;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Authentication;

/// <summary>
/// Sign in with Apple server-to-server notifications (openapi receiveAppleNotification). Apple posts a JWT signed
/// with its sign-in keys when a user changes how they use Sign in with Apple for this app.
/// </summary>
public static partial class AppleNotificationEndpoints
{
    public sealed record AppleNotification(string? Payload);

    public static void MapAppleNotifications(this RouteGroupBuilder v1) =>
        v1.MapPost("/webhooks/apple", ReceiveAsync).AllowAnonymous();

    private static async Task<IResult> ReceiveAsync(
        AppleNotification notification, IIdentityTokenValidator validator, NpgsqlDataSource db,
        TimeProvider time, ILogger<AppleNotification> logger, CancellationToken cancellationToken)
    {
        var claims = string.IsNullOrEmpty(notification.Payload)
            ? null
            : await validator.ValidateProviderJwtAsync("apple", notification.Payload, cancellationToken);
        if (claims is null || ReadEvent(claims.GetValueOrDefault("events")) is not { } appleEvent)
        {
            throw ApiProblemException.Unauthorized("The notification is not a valid Apple notification for this app.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var affected = appleEvent.Type switch
        {
            // The user stopped using Sign in with Apple with this app: sign them out everywhere. The identity
            // stays, so signing in with Apple again returns to the same account.
            "consent-revoked" => await connection.ExecuteAsync(
                """
                UPDATE auth_sessions SET revoked_at = @now, revoked_reason = 'provider_revoked'
                WHERE revoked_at IS NULL
                  AND user_id = (SELECT user_id FROM auth_identities WHERE provider = 'apple' AND subject = @Subject)
                """,
                new { now, appleEvent.Subject }),
            // The Apple ID no longer exists, so the account can never be signed in to again (one sign-in per
            // account, ADR 0003). Delete it the same way as DELETE /me.
            "account-delete" => await connection.ExecuteAsync(
                """
                UPDATE users SET deleted_at = @now
                WHERE deleted_at IS NULL
                  AND id = (SELECT user_id FROM auth_identities WHERE provider = 'apple' AND subject = @Subject)
                """,
                new { now, appleEvent.Subject }),
            // email-disabled / email-enabled: Apple stopped or resumed forwarding to the private relay address.
            // Nothing depends on it yet.
            _ => 0,
        };

        LogNotification(logger, appleEvent.Type, affected);
        return Results.Ok();
    }

    private sealed record AppleEvent(string Type, string Subject);

    // Apple sends "events" as a JSON string inside the JWT.
    private static AppleEvent? ReadEvent(object? events)
    {
        try
        {
            using var document = events switch
            {
                string json => JsonDocument.Parse(json),
                JsonElement { ValueKind: JsonValueKind.String } element => JsonDocument.Parse(element.GetString()!),
                null => null,
                _ => JsonDocument.Parse(JsonSerializer.Serialize(events)),
            };
            if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return new AppleEvent(type.GetString()!, sub.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Apple notification {Type} affected {Rows} rows.")]
    private static partial void LogNotification(ILogger logger, string type, int rows);
}
