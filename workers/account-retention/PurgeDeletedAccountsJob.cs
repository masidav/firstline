using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.AccountRetention;

/// <summary>
/// Hard-deletes the content of accounts deleted more than 30 days ago (decided 2026-10-18), using
/// <c>purge_deleted_user()</c>: logs, lists, submissions, and comments go; comments others replied to become
/// "[deleted]" tombstones; ledger rows stay, reversed. Runs hourly and is safe to repeat.
/// </summary>
public sealed partial class PurgeDeletedAccountsJob(NpgsqlDataSource db, ILogger<PurgeDeletedAccountsJob> logger) : IJobHandler
{
    /// <summary>Same as the database function's default.</summary>
    public const string RetentionWindow = "30 days";

    private const int BatchSize = 200;

    public string Kind => "accounts.purge_deleted";

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var due = await connection.QueryAsync<Guid>(
            """
            SELECT id FROM users
            WHERE deleted_at <= now() - @RetentionWindow::interval AND content_purged_at IS NULL
            ORDER BY deleted_at
            LIMIT @BatchSize
            """,
            new { RetentionWindow, BatchSize });

        var purged = 0;
        var blocked = 0;
        foreach (var userId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // One statement, so each account is purged in its own transaction.
                if (await connection.ExecuteScalarAsync<bool>(
                        "SELECT purge_deleted_user(@userId, @RetentionWindow::interval)", new { userId, RetentionWindow }))
                {
                    purged++;
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException)
            {
                // E.g. stored submission files not yet removed from object storage. Others still go ahead;
                // the next run tries again.
                blocked++;
                LogBlocked(logger, userId, e.MessageText);
            }
        }

        LogPurged(logger, purged, blocked);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged content of {Purged} deleted accounts ({Blocked} blocked).")]
    private static partial void LogPurged(ILogger logger, int purged, int blocked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not purge deleted account {UserId}: {Reason}")]
    private static partial void LogBlocked(ILogger logger, Guid userId, string reason);
}
