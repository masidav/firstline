using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.Notifications;

/// <summary>
/// Every 15 minutes: reminds fans of presales that open within a day for shows by artists they have a score for, in
/// their hub cities (<c>notify_presales()</c>, migration 0031). A presale is a time, not a change, so no trigger can
/// do it. Idempotent: each fan hears about each presale once. Fans turn it off with <c>presale_reminders</c>.
/// </summary>
public sealed partial class PresaleReminderJob(NpgsqlDataSource db, ILogger<PresaleReminderJob> logger) : IJobHandler
{
    public string Kind => "notifications.presales";

    public TimeSpan? Every => TimeSpan.FromMinutes(15);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>("SELECT notify_presales()");
        if (count > 0)
        {
            LogReminded(logger, count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} presale reminders.")]
    private static partial void LogReminded(ILogger logger, int count);
}
