using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.Fraud;

/// <summary>
/// Every 6 hours, <c>fraud.detect</c>: runs the fraud rules (<c>fraud_rules</c>, migrations 0043 and 0045) over every fan
/// with plays or saves in the last day and raises a rule flag for staff to review where one fires (<c>run_fraud_detection()</c>).
/// An open rule flag only blocks registering for fan tickets until staff uphold it. Idempotent: a fan already held is
/// skipped, and a cleared rule waits out its cooldown.
/// </summary>
public sealed partial class DetectFraudJob(NpgsqlDataSource db, ILogger<DetectFraudJob> logger) : IJobHandler
{
    public string Kind => "fraud.detect";

    public TimeSpan? Every => TimeSpan.FromHours(6);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var flagged = await connection.ExecuteScalarAsync<int>("SELECT run_fraud_detection(interval '1 day')", commandTimeout: 600);
        LogFlagged(logger, flagged);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fraud rules flagged {Count} fans for review.")]
    private static partial void LogFlagged(ILogger logger, int count);
}
