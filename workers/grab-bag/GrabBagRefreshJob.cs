using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.GrabBag;

/// <summary>
/// Daily: re-reads from Spotify the grab-bag songs not refreshed for <see cref="RefreshEvery"/> (oldest first, a
/// batch at a time), so renamed tracks and new artwork show up (migration 0019). A track Spotify no longer has keeps
/// its stored details and is stamped so it isn't retried every day. A Spotify outage fails the job, which retries.
/// </summary>
public sealed partial class GrabBagRefreshJob(
    NpgsqlDataSource db, SpotifyClient spotify, IOptions<SpotifyOptions> options, ILogger<GrabBagRefreshJob> logger) : IJobHandler
{
    public const string JobKind = "grab_bag.refresh_songs";

    public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(7);

    private const int BatchSize = 200;

    public string Kind => JobKind;

    public TimeSpan? Every => TimeSpan.FromDays(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!options.Value.IsCatalogEnabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var due = (await connection.QueryAsync<(Guid Id, string SpotifyTrackId)>(
            """
            SELECT id, spotify_track_id FROM grab_bag_songs
            WHERE COALESCE(refreshed_at, added_at) < now() - @every
            ORDER BY COALESCE(refreshed_at, added_at)
            LIMIT @BatchSize
            """,
            new { every = RefreshEvery, BatchSize })).ToList();

        var updated = 0;
        var gone = 0;
        foreach (var (id, spotifyTrackId) in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = await spotify.GetTrackAsync(spotifyTrackId, cancellationToken);
            if (track is null)
            {
                gone++;
                await connection.ExecuteAsync("UPDATE grab_bag_songs SET refreshed_at = now() WHERE id = @id", new { id });
                continue;
            }

            updated++;
            await connection.ExecuteAsync(
                """
                UPDATE grab_bag_songs
                SET title = @title, album_title = @albumTitle, cover_url = @coverUrl, refreshed_at = now()
                WHERE id = @id
                """,
                new { id, title = track.Name, albumTitle = track.AlbumName, coverUrl = track.CoverUrl });
        }

        LogRefreshed(logger, updated, gone);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed {Updated} grab-bag songs; {Gone} are no longer on Spotify.")]
    private static partial void LogRefreshed(ILogger logger, int updated, int gone);
}
