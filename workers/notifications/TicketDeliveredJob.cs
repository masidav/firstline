using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.Notifications;

/// <summary>
/// Every 15 minutes: tells holders their ticket has been delivered once a reservation's <c>deliver_at</c> has passed
/// (<c>notify_delivered_tickets()</c>, migration 0021). Delivery is a time, not a change, so no trigger can do it.
/// Idempotent: each reservation is announced once.
/// </summary>
public sealed partial class TicketDeliveredJob(NpgsqlDataSource db, ILogger<TicketDeliveredJob> logger) : IJobHandler
{
    public string Kind => "notifications.tickets_delivered";

    public TimeSpan? Every => TimeSpan.FromMinutes(15);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>("SELECT notify_delivered_tickets()");
        if (count > 0)
        {
            LogDelivered(logger, count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Announced {Count} delivered tickets.")]
    private static partial void LogDelivered(ILogger logger, int count);
}
