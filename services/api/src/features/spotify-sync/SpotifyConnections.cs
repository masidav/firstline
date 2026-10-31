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

    /// <summary>
    /// Queues a sync of the fan's library unless one ran or was asked for in the last <paramref name="interval"/>
    /// (the app's quiet sync on opening, decided 2026-10-25; also after a Like, 2026-10-31). Never an error: without a
    /// connection, with a lapsed authorization, or with a sync already queued or running, nothing happens. True when
    /// this call queued a sync.
    /// </summary>
    public static async Task<bool> RequestBackgroundSyncAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, TimeSpan interval)
    {
        var state = await connection.QuerySingleOrDefaultAsync<BackgroundSyncState>(
            """
            SELECT sync_state AS SyncState,
                   (sync_requested_at > now() - @interval OR last_synced_at > now() - @interval) AS Recent
            FROM spotify_connections WHERE user_id = @userId FOR UPDATE
            """,
            new { userId, interval }, transaction);
        switch (state)
        {
            case null or { SyncState: "reauth_required" or "queued" or "running" } or { Recent: true }:
                return false;
        }

        await connection.ExecuteAsync(
            """
            UPDATE spotify_connections SET sync_requested_at = now() WHERE user_id = @userId;
            SELECT request_spotify_sync(@userId);
            """,
            new { userId }, transaction);
        return true;
    }

    private sealed record BackgroundSyncState(string SyncState, bool? Recent);

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
