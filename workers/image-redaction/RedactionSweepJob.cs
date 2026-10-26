using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>
/// Hourly safety net for redaction (migration 0029):
/// <list type="bullet">
/// <item>queues <see cref="RedactSubmissionJob"/> for every verified, not taken-down submission with a file still
/// <c>pending</c> or <c>approved</c> (a missed trigger, a job that ran out of attempts, a submission seeded as
/// verified);</item>
/// <item>warns when verified files are still in quarantine <see cref="OverdueAfter"/> after upload. The quarantine
/// bucket deletes every file at 30 days (decided 2026-10-25), so these are about to be lost. The warning's text is
/// what the Cloud Monitoring alert matches (infrastructure/terraform/modules/environment/monitoring.tf).</item>
/// </list>
/// </summary>
public sealed partial class RedactionSweepJob(NpgsqlDataSource db, ILogger<RedactionSweepJob> logger) : IJobHandler
{
    public const string JobKind = "redaction.sweep";

    public static readonly TimeSpan OverdueAfter = TimeSpan.FromDays(25);

    public string Kind => JobKind;

    public TimeSpan? Every => TimeSpan.FromHours(1);

    private const string Eligible =
        """
        s.status = 'verified' AND s.removed_at IS NULL
        """;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<int>(
            $"""
            SELECT count(enqueue_job(@kind, jsonb_build_object('submission_id', s.id), 'redact:' || s.id))
            FROM submissions s
            WHERE {Eligible}
              AND EXISTS (SELECT 1 FROM submission_files f
                          WHERE f.submission_id = s.id AND f.redaction_state IN ('pending', 'approved'))
            """,
            new { kind = RedactSubmissionJob.JobKind });

        var overdue = await connection.QuerySingleAsync<(int Files, int NeedsReview)>(
            $"""
            SELECT count(*)::int, count(*) FILTER (WHERE f.redaction_state = 'needs_review')::int
            FROM submission_files f JOIN submissions s ON s.id = f.submission_id
            WHERE {Eligible} AND f.redaction_state <> 'archived' AND f.uploaded_at < now() - @overdueAfter
            """,
            new { overdueAfter = OverdueAfter });

        if (queued > 0)
        {
            LogQueued(logger, queued);
        }

        if (overdue.Files > 0)
        {
            LogOverdue(logger, overdue.Files, overdue.NeedsReview);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Redaction sweep queued {Count} submissions.")]
    private static partial void LogQueued(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Redaction overdue: {Files} verified files are still in quarantine 25 days after upload ({NeedsReview} wait for staff). The quarantine bucket deletes them at 30 days.")]
    private static partial void LogOverdue(ILogger logger, int files, int needsReview);
}
