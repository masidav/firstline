using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.AccountRetention;

/// <summary>
/// Housekeeping retention (migration 0053, privacy README "Retention"): <c>prune_expired_records()</c> deletes sessions
/// expired or revoked more than 30 days ago (with their refresh tokens), idempotency keys after 24 hours, push delivery
/// records after 30 days, and push devices disabled more than 30 days ago. <c>prune_client_signals()</c> (migration 0054)
/// deletes the fraud rules' client signals past their period: address hashes after 30 days (and their daily salts after
/// 2), push device history after 90, Spotify account history after 180, eligibility checks after 90. Finished jobs are
/// pruned by <see cref="PruneJobsJob"/>. Runs daily; deleting what is past its period is safe to repeat.
/// </summary>
public sealed partial class PruneExpiredRecordsJob(NpgsqlDataSource db, ILogger<PruneExpiredRecordsJob> logger) : IJobHandler
{
    public string Kind => "privacy.retention";

    public TimeSpan? Every => TimeSpan.FromDays(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var removed = (await connection.QueryAsync<(string Record, int Removed)>(
                new CommandDefinition("SELECT record, removed FROM prune_expired_records()", cancellationToken: cancellationToken, commandTimeout: 600)))
            .ToDictionary(r => r.Record, r => r.Removed);
        var (sessions, keys, deliveries, devices) = (removed.GetValueOrDefault("auth_sessions"), removed.GetValueOrDefault("idempotency_keys"),
            removed.GetValueOrDefault("push_deliveries"), removed.GetValueOrDefault("push_devices"));
        LogPruned(logger, sessions, keys, deliveries, devices);
        var signals = (await connection.QueryAsync<(string Record, int Removed)>(
                new CommandDefinition("SELECT record, removed FROM prune_client_signals()", cancellationToken: cancellationToken, commandTimeout: 600)))
            .Sum(r => r.Removed);
        LogPrunedSignals(logger, signals);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: removed {Signals} expired client signal rows (address hashes, device and Spotify history, eligibility checks).")]
    private static partial void LogPrunedSignals(ILogger logger, int signals);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention: removed {Sessions} old sessions, {IdempotencyKeys} idempotency keys, {PushDeliveries} push delivery records, and {PushDevices} disabled push devices.")]
    private static partial void LogPruned(ILogger logger, int sessions, int idempotencyKeys, int pushDeliveries, int pushDevices);
}
