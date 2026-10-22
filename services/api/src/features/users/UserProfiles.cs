using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Users;

/// <summary>openapi Me.</summary>
public sealed record MeResponse(
    Guid Id, string Handle, string Email, string? DisplayName, string? AvatarUrl, bool IsStaff, DateTime CreatedAt)
{
    public SpotifyStatus Spotify { get; init; } = SpotifyStatus.NotConnected;

    /// <summary>Primary first. Empty until the fan picks them (the app asks right after sign-up).</summary>
    public IReadOnlyList<HubCity> HubCities { get; init; } = [];

    /// <summary>When the hub cities may next be changed; null when they may be changed now.</summary>
    public DateTime? HubCitiesChangeAvailableAt { get; init; }
}

public static partial class UserProfiles
{
    // Same rule as the users_handle_format constraint and openapi SessionRequest.handle.
    [GeneratedRegex("^[A-Za-z0-9_]{3,30}$")]
    public static partial Regex HandlePattern();

    public const string HandleRule = "Use 3 to 30 letters, digits, or underscores.";

    public const int DisplayNameMaxLength = 60;

    public static ApiProblemException HandleTaken() =>
        new(StatusCodes.Status409Conflict, "handle_taken", "Handle already taken", "Choose a different handle.");

    /// <summary>The signed-in user's own profile, or null if the account is gone.</summary>
    public static async Task<MeResponse?> LoadMeAsync(NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        var me = await connection.QuerySingleOrDefaultAsync<MeResponse>(
            """
            SELECT id AS Id, handle::text AS Handle, email::text AS Email, display_name AS DisplayName,
                   avatar_url AS AvatarUrl, is_staff AS IsStaff, created_at AS CreatedAt
            FROM users WHERE id = @userId AND deleted_at IS NULL
            """,
            new { userId }, transaction);
        if (me is null)
        {
            return null;
        }

        var (hubs, changeAvailableAt) = await HubCities.LoadAsync(connection, userId, transaction);
        return me with
        {
            Spotify = await SpotifyConnections.LoadStatusAsync(connection, userId, transaction),
            HubCities = hubs,
            HubCitiesChangeAvailableAt = changeAvailableAt,
        };
    }
}
