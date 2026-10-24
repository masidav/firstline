using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Users;

/// <summary>openapi Me.</summary>
/// <remarks>
/// The band links are only kept while <paramref name="IsArtist"/> is true. Social handles are stored without the @.
/// Everything the query returns is a constructor parameter (Dapper maps a record by its constructor).
/// </remarks>
public sealed record MeResponse(
    Guid Id, string Handle, string Email, string? DisplayName, string? AvatarUrl, bool IsStaff, DateTime CreatedAt,
    bool IsArtist, string? BandUrl, string? InstagramHandle, string? XHandle)
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

    // Band links (users_band_url_check, users_instagram_handle_check, users_x_handle_check in migration 0016).
    public const int BandUrlMaxLength = 200;

    [GeneratedRegex(@"^https://[^\s/]+")]
    public static partial Regex BandUrlPattern();

    public const string BandUrlRule = "Use an https:// link of at most 200 characters, or null to clear it.";

    [GeneratedRegex("^[A-Za-z0-9._]{1,30}$")]
    public static partial Regex InstagramHandlePattern();

    public const string InstagramHandleRule = "Use 1 to 30 letters, digits, dots, or underscores, or null to clear it.";

    [GeneratedRegex("^[A-Za-z0-9_]{1,15}$")]
    public static partial Regex XHandlePattern();

    public const string XHandleRule = "Use 1 to 15 letters, digits, or underscores, or null to clear it.";

    public const string LinksNeedArtistRule = "Band links need is_artist to be true.";

    public static ApiProblemException HandleTaken() =>
        new(StatusCodes.Status409Conflict, "handle_taken", "Handle already taken", "Choose a different handle.");

    /// <summary>The signed-in user's own profile, or null if the account is gone.</summary>
    public static async Task<MeResponse?> LoadMeAsync(NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        var me = await connection.QuerySingleOrDefaultAsync<MeResponse>(
            """
            SELECT id AS Id, handle::text AS Handle, email::text AS Email, display_name AS DisplayName,
                   avatar_url AS AvatarUrl, is_staff AS IsStaff, created_at AS CreatedAt,
                   is_artist AS IsArtist, band_url AS BandUrl, instagram_handle AS InstagramHandle, x_handle AS XHandle
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
