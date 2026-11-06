using Dapper;
using FandomScore.MusicProviders;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.MusicSync;

/// <summary>
/// Hourly: queues a sync for each Apple Music or Tidal connection whose last attempt is older than
/// <see cref="MusicProviderOptions.SyncEvery"/>. Connections waiting for the fan to reconnect are skipped; failed ones
/// are tried again on the same schedule. A provider that isn't configured is left alone.
/// </summary>
public sealed partial class ScheduleMusicSyncsJob(
    NpgsqlDataSource db, IOptions<MusicProviderOptions> options, ILogger<ScheduleMusicSyncsJob> logger) : IJobHandler
{
    private const int BatchSize = 500;

    public string Kind => "music.schedule_syncs";

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var providers = MusicProvider.All.Where(options.Value.IsEnabled).ToArray();
        if (providers.Length == 0)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FILTER (WHERE request_music_sync(user_id, provider))::integer
            FROM (SELECT user_id, provider FROM music_connections
                  WHERE provider = ANY(@providers) AND sync_state IN ('idle', 'failed')
                    AND COALESCE(last_sync_attempt_at, '-infinity') < now() - @every
                  ORDER BY last_sync_attempt_at NULLS FIRST
                  LIMIT @BatchSize) due
            """,
            new { providers, every = options.Value.SyncEvery, BatchSize });
        LogQueued(logger, queued);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} scheduled Apple Music and Tidal syncs.")]
    private static partial void LogQueued(ILogger logger, int count);
}
