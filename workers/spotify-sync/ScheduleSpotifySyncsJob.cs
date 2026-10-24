using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.SpotifySync;

/// <summary>
/// Hourly: queues a sync for each connection whose last attempt is older than <see cref="SpotifyOptions.SyncEvery"/>,
/// and a play poll (<see cref="PollPlaysJob"/>) for each synced connection not polled within
/// <see cref="SpotifyOptions.PollPlaysEvery"/>. Connections waiting for the user to reconnect are skipped. Failed
/// ones are tried again on the same schedule.
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
        // Polls only make sense once a sync has seeded the ledger; a running sync holds the lock, so skip it.
        var polls = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(enqueue_job(@kind, jsonb_build_object('user_id', user_id), @kind || ':' || user_id))::integer
            FROM (SELECT user_id FROM spotify_connections
                  WHERE sync_state IN ('idle', 'failed') AND last_synced_at IS NOT NULL
                    AND COALESCE(plays_polled_at, '-infinity') < now() - @every
                  ORDER BY plays_polled_at NULLS FIRST
                  LIMIT @BatchSize) due
            """,
            new { kind = PollPlaysJob.JobKind, every = options.Value.PollPlaysEvery, BatchSize });
        LogQueued(logger, queued, polls);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} scheduled Spotify syncs and {Polls} play polls.")]
    private static partial void LogQueued(ILogger logger, int count, int polls);
}
