using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.SpotifySync;

/// <summary>
/// Hourly: queues a sync for each connection whose last attempt is older than <see cref="SpotifyOptions.SyncEvery"/>.
/// Connections waiting for the user to reconnect are skipped. Failed ones are tried again on the same schedule.
/// </summary>
public sealed partial class ScheduleSpotifySyncsJob(
    NpgsqlDataSource db, IOptions<SpotifyOptions> options, ILogger<ScheduleSpotifySyncsJob> logger) : IJobHandler
{
    private const int BatchSize = 500;

    public string Kind => "spotify.schedule_syncs";

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!options.Value.IsEnabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FILTER (WHERE request_spotify_sync(user_id))::integer
            FROM (SELECT user_id FROM spotify_connections
                  WHERE sync_state IN ('idle', 'failed')
                    AND COALESCE(last_sync_attempt_at, '-infinity') < now() - @every
                  ORDER BY last_sync_attempt_at NULLS FIRST
                  LIMIT @BatchSize) due
            """,
            new { every = options.Value.SyncEvery, BatchSize });
        LogQueued(logger, queued);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} scheduled Spotify syncs.")]
    private static partial void LogQueued(ILogger logger, int count);
}
