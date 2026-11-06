using Dapper;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.AccountRetention;

/// <summary>
/// Housekeeping retention (migration 0053, privacy README "Retention"): <c>prune_expired_records()</c> deletes sessions
/// expired or revoked more than 30 days ago (with their refresh tokens), idempotency keys after 24 hours, push and email
/// delivery records after 30 days, and push devices disabled more than 30 days ago. <c>prune_client_signals()</c>
/// (migration 0054) deletes the fraud rules' client signals past their period: address hashes after 30 days (and their
/// daily salts after 2), push device history after 90, Spotify account history after 180, eligibility checks after 90.
/// <c>prune_optional_records()</c> (migration 0060) prunes the records whose period is still an owner decision, each
/// only once its <see cref="RetentionOptions"/> period is set. Finished jobs are pruned by <see cref="PruneJobsJob"/>.
/// Runs daily; deleting what is past its period is safe to repeat.
/// </summary>
public sealed partial class PruneExpiredRecordsJob(NpgsqlDataSource db, IOptions<RetentionOptions> retention, ILogger<PruneExpiredRecordsJob> logger) : IJobHandler
{
    private readonly RetentionOptions _retention = retention.Value;

    public string Kind => "privacy.retention";

    public TimeSpan? Every => TimeSpan.FromDays(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var removed = (await connection.QueryAsync<(string Record, int Removed)>(
                new CommandDefinition("SELECT record, removed FROM prune_expired_records()", cancellationToken: cancellationToken, commandTimeout: 600)))
            .ToDictionary(r => r.Record, r => r.Removed);
        var (sessions, keys, deliveries, devices, emails) = (removed.GetValueOrDefault("auth_sessions"), removed.GetValueOrDefault("idempotency_keys"),
            removed.GetValueOrDefault("push_deliveries"), removed.GetValueOrDefault("push_devices"), removed.GetValueOrDefault("email_deliveries"));
        LogPruned(logger, sessions, keys, deliveries, devices, emails);
        var signals = (await connection.QueryAsync<(string Record, int Removed)>(
                new CommandDefinition("SELECT record, removed FROM prune_client_signals()", cancellationToken: cancellationToken, commandTimeout: 600)))
            .Sum(r => r.Removed);
        LogPrunedSignals(logger, signals);

        if (!_retention.Any)
        {
            return;
        }

        var optional = (await connection.QueryAsync<(string Record, int Removed)>(
                new CommandDefinition(
                    "SELECT record, removed FROM prune_optional_records(@readNotifications, @notifications, @clickLinks, @ocrText)",
                    new
                    {
                        readNotifications = _retention.ReadNotifications,
                        notifications = _retention.Notifications,
                        clickLinks = _retention.ClickLinks,
                        ocrText = _retention.OcrText,
                    },
                    cancellationToken: cancellationToken, commandTimeout: 600)))
            .ToDictionary(r => r.Record, r => r.Removed);
        var (readNotifications, notifications, clickLinks, ocrText) = (optional.GetValueOrDefault("read_notifications"),
            optional.GetValueOrDefault("notifications"), optional.GetValueOrDefault("click_links"), optional.GetValueOrDefault("ocr_text"));
        LogPrunedOptional(logger, readNotifications, notifications, clickLinks, ocrText);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: removed {Signals} expired client signal rows (address hashes, device and Spotify history, eligibility checks).")]
    private static partial void LogPrunedSignals(ILogger logger, int signals);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention: removed {Sessions} old sessions, {IdempotencyKeys} idempotency keys, {PushDeliveries} push delivery records, {PushDevices} disabled push devices, and {EmailDeliveries} email delivery records.")]
    private static partial void LogPruned(ILogger logger, int sessions, int idempotencyKeys, int pushDeliveries, int pushDevices, int emailDeliveries);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention (Retention__* settings): removed {ReadNotifications} read notifications and {Notifications} old ones, unlinked {ClickLinks} outbound clicks, cleared OCR text on {OcrText} submissions.")]
    private static partial void LogPrunedOptional(ILogger logger, int readNotifications, int notifications, int clickLinks, int ocrText);
}
