using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Notifications;

/// <summary>
/// The phone app's push tokens (migration 0032). The app registers its Expo push token on every launch once the fan
/// allows notifications, and removes it on sign-out. A token moves to whoever signed in last on that phone. The worker
/// sends every in-app notification to the fan's live devices (workers/notifications/PushNotificationJob).
/// </summary>
public static partial class PushDeviceEndpoints
{
    public static void MapPushDevices(this RouteGroupBuilder v1)
    {
        v1.MapPut("/me/push-devices", RegisterAsync);
        v1.MapDelete("/me/push-devices/{deviceId:guid}", RemoveAsync);
    }

    public sealed record PushDevice(Guid Id, string Platform, DateTime CreatedAt, DateTime LastSeenAt);

    private static async Task<IResult> RegisterAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        string? Field(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
                                      && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var token = Field("token");
        var platform = Field("platform");
        var errors = new List<FieldError>();
        if (token is null || !ExpoToken().IsMatch(token))
        {
            errors.Add(new FieldError("token", "Send the Expo push token, like ExponentPushToken[...]."));
        }

        if (platform is not ("ios" or "android"))
        {
            errors.Add(new FieldError("platform", "Use ios or android."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var device = await connection.QuerySingleAsync<PushDevice>(
            """
            INSERT INTO push_devices (user_id, token, platform) VALUES (@userId, @token, @platform)
            ON CONFLICT ON CONSTRAINT push_devices_token_key DO UPDATE
              SET user_id = EXCLUDED.user_id, platform = EXCLUDED.platform, last_seen_at = now(),
                  disabled_at = NULL, disabled_reason = NULL,
                  created_at = CASE WHEN push_devices.user_id = EXCLUDED.user_id THEN push_devices.created_at ELSE now() END
            RETURNING id AS Id, platform AS Platform, created_at AS CreatedAt, last_seen_at AS LastSeenAt
            """,
            new { userId = user.UserId(), token, platform });
        return Results.Ok(device);
    }

    private static async Task<IResult> RemoveAsync(
        ClaimsPrincipal user, Guid deviceId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        // Idempotent; only the caller's own devices.
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM push_devices WHERE id = @deviceId AND user_id = @userId",
            new { deviceId, userId = user.UserId() });
        return Results.NoContent();
    }

    [GeneratedRegex(@"^Expo(nent)?PushToken\[[A-Za-z0-9_-]{10,200}\]$")]
    private static partial Regex ExpoToken();
}
