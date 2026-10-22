using Dapper;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.SpotifySync;

/// <summary>openapi SpotifyStatus. <see cref="SyncError"/> is a stable code (see migration 0005).</summary>
public sealed record SpotifyStatus(
    bool Connected, string[] Scopes, DateTime? ConnectedAt, DateTime? LastSyncedAt, string SyncState, string? SyncError,
    int TrackedArtistsFound, int TopArtistsAdded)
{
    public static readonly SpotifyStatus NotConnected = new(false, [], null, null, "idle", null, 0, 0);
}

public static class SpotifyConnections
{
    public static async Task<SpotifyStatus> LoadStatusAsync(
        NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StatusRow>(
            """
            SELECT scopes AS Scopes, connected_at AS ConnectedAt, last_synced_at AS LastSyncedAt,
                   sync_state AS SyncState, sync_error AS SyncError, tracked_artists_found AS TrackedArtistsFound,
                   top_artists_added AS TopArtistsAdded
            FROM spotify_connections WHERE user_id = @userId
            """,
            new { userId }, transaction);
        return row is null
            ? SpotifyStatus.NotConnected
            : new SpotifyStatus(true, row.Scopes, row.ConnectedAt, row.LastSyncedAt, row.SyncState, row.SyncError, row.TrackedArtistsFound,
                row.TopArtistsAdded);
    }

    // Dapper cannot pass a text[] column to a record constructor, so it fills properties here.
    private sealed class StatusRow
    {
        public string[] Scopes { get; init; } = [];

        public DateTime ConnectedAt { get; init; }

        public DateTime? LastSyncedAt { get; init; }

        public string SyncState { get; init; } = "";

        public string? SyncError { get; init; }

        public int TrackedArtistsFound { get; init; }

        public int TopArtistsAdded { get; init; }
    }

    public static ApiProblemException NotConnected() =>
        new(StatusCodes.Status409Conflict, "spotify_not_connected", "Spotify is not connected", "Connect Spotify first.");

    public static ApiProblemException ReauthRequired() =>
        new(StatusCodes.Status409Conflict, "spotify_reauth_required", "Spotify needs to be connected again",
            "Spotify no longer accepts this connection. Connect Spotify again.");

    public static ApiProblemException InUse() =>
        new(StatusCodes.Status409Conflict, "spotify_in_use", "Spotify account already connected",
            "This Spotify account is connected to another account here.");

    public static ApiProblemException Unavailable(string detail, TimeSpan? retryAfter = null) =>
        new(StatusCodes.Status503ServiceUnavailable, "spotify_unavailable", "Spotify is unavailable", detail)
        {
            RetryAfter = retryAfter,
        };
}
