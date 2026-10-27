using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.Jobs;

/// <summary>A job a worker holds the lease on. <see cref="Payload"/> is JSON.</summary>
public sealed record ClaimedJob(long Id, string Kind, string Payload, int Attempts, int MaxAttempts);

public sealed class JobOptions
{
    public const string Section = "Jobs";

    /// <summary>How long to wait before polling again when no job is ready.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A job not finished within its lease is assumed abandoned and runs again.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Jobs run at the same time per worker process.</summary>
    public int Concurrency { get; set; } = 2;

    public TimeSpan KeepSucceeded { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan KeepFailed { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Comma-separated job kinds this process runs; empty runs every kind. Lets the one worker image run as several
    /// deployments with different identities: the <c>redaction</c> deployment runs only the kinds that touch
    /// submission files, and the main worker excludes them with <see cref="ExceptKinds"/>.
    /// </summary>
    public string OnlyKinds { get; set; } = "";

    /// <summary>Comma-separated job kinds this process never claims (another deployment runs them).</summary>
    public string ExceptKinds { get; set; } = "";

    public JobKindFilter KindFilter() => JobKindFilter.Parse(OnlyKinds, ExceptKinds);
}

/// <summary>Which job kinds a worker process claims (<see cref="JobOptions.OnlyKinds"/>, <see cref="JobOptions.ExceptKinds"/>).</summary>
public sealed record JobKindFilter(string[] Only, string[] Except)
{
    public static JobKindFilter Parse(string? only, string? except)
    {
        var filter = new JobKindFilter(Split(only), Split(except));
        if (filter.Only.Length > 0 && filter.Except.Length > 0)
        {
            throw new InvalidOperationException("Set Jobs:OnlyKinds or Jobs:ExceptKinds, not both.");
        }

        return filter;
    }

    public bool Runs(string kind) =>
        (Only.Length == 0 || Only.Contains(kind, StringComparer.Ordinal)) && !Except.Contains(kind, StringComparer.Ordinal);

    private static string[] Split(string? kinds) =>
        (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// The Postgres job queue (database/migrations/0004_jobs_and_retention.sql, ADR 0004). The API and SQL code
/// enqueue with <c>enqueue_job()</c> inside their own transactions; workers claim, run, and finish jobs here.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a job queue, not a collection type.")]
public sealed class JobQueue(NpgsqlDataSource db, IOptions<JobOptions> options)
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromHours(1);

    private readonly JobOptions _options = options.Value;
    private readonly JobKindFilter _kinds = options.Value.KindFilter();

    public string WorkerId { get; } = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";

    /// <summary>Returns the job id, or null when a queued job with the same dedupe key exists.</summary>
    public async Task<long?> EnqueueAsync(
        string kind, object? payload = null, string? dedupeKey = null, DateTimeOffset? runAt = null, int maxAttempts = 10)
    {
        await using var connection = await db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long?>(
            "SELECT enqueue_job(@kind, @payload::jsonb, @dedupeKey, COALESCE(@runAt, now()), @maxAttempts)",
            new { kind, payload = JsonSerializer.Serialize(payload ?? new { }), dedupeKey, runAt, maxAttempts });
    }

    /// <summary>
    /// Claims the oldest ready job, or one whose worker's lease ran out, among the kinds this process runs
    /// (<see cref="JobOptions.OnlyKinds"/>, <see cref="JobOptions.ExceptKinds"/>). Null when none is ready.
    /// </summary>
    public async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ClaimedJob>(
            """
            UPDATE jobs j SET status = 'running', attempts = j.attempts + 1,
                              locked_by = @WorkerId, locked_until = now() + @leaseSeconds * interval '1 second'
            FROM (SELECT id FROM jobs
                  WHERE ((status = 'queued' AND run_at <= now()) OR (status = 'running' AND locked_until < now()))
                    AND (cardinality(@only::text[]) = 0 OR kind = ANY(@only::text[]))
                    AND NOT (kind = ANY(@except::text[]))
                  ORDER BY run_at
                  LIMIT 1
                  FOR UPDATE SKIP LOCKED) ready
            WHERE j.id = ready.id
            RETURNING j.id AS Id, j.kind AS Kind, j.payload::text AS Payload, j.attempts AS Attempts,
                      j.max_attempts AS MaxAttempts
            """,
            new { WorkerId, leaseSeconds = _options.Lease.TotalSeconds, only = _kinds.Only, except = _kinds.Except });
    }

    public async Task CompleteAsync(long jobId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE jobs SET status = 'succeeded', finished_at = now(), locked_by = NULL, locked_until = NULL
            WHERE id = @jobId AND locked_by = @WorkerId AND status = 'running'
            """,
            new { jobId, WorkerId });
    }

    /// <summary>
    /// Records the failure and retries later with exponential backoff, or gives up after the last attempt
    /// (or at once when <paramref name="permanent"/>).
    /// </summary>
    public async Task FailAsync(ClaimedJob job, Exception error, bool permanent = false)
    {
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE jobs SET status = CASE WHEN @giveUp THEN 'failed' ELSE 'queued' END,
                            finished_at = CASE WHEN @giveUp THEN now() END,
                            run_at = now() + @retrySeconds * interval '1 second',
                            last_error = @lastError, locked_by = NULL, locked_until = NULL
            WHERE id = @Id AND locked_by = @WorkerId AND status = 'running'
            """,
            new
            {
                job.Id,
                WorkerId,
                giveUp = permanent || job.Attempts >= job.MaxAttempts,
                retrySeconds = RetryDelay(job.Attempts).TotalSeconds,
                lastError = Truncate($"{error.GetType().Name}: {error.Message}", 2000),
            });
    }

    /// <summary>Hands a job back without counting the attempt, e.g. when the worker is shutting down.</summary>
    public async Task ReleaseAsync(long jobId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE jobs SET status = 'queued', attempts = attempts - 1, locked_by = NULL, locked_until = NULL
            WHERE id = @jobId AND locked_by = @WorkerId AND status = 'running'
            """,
            new { jobId, WorkerId });
    }

    /// <summary>Creates or updates a recurring job. A new schedule is due at once.</summary>
    public async Task RegisterScheduleAsync(string kind, TimeSpan every, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO job_schedules (kind, every) VALUES (@kind, @everySeconds * interval '1 second')
            ON CONFLICT (kind) DO UPDATE SET every = EXCLUDED.every
            """,
            new { kind, everySeconds = every.TotalSeconds });
    }

    /// <summary>Enqueues each due recurring job once, even with several workers.</summary>
    public async Task<int> FireDueSchedulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT fire_due_schedules()");
    }

    /// <summary>Deletes finished jobs past their retention.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            """
            DELETE FROM jobs
            WHERE (status = 'succeeded' AND finished_at < now() - @keepSucceeded * interval '1 second')
               OR (status = 'failed'    AND finished_at < now() - @keepFailed * interval '1 second')
            """,
            new { keepSucceeded = _options.KeepSucceeded.TotalSeconds, keepFailed = _options.KeepFailed.TotalSeconds });
    }

    /// <summary>30 s, 1 min, 2 min, ... capped at 1 hour, with ±20% jitter so retries don't arrive together.</summary>
    public static TimeSpan RetryDelay(int attempts)
    {
        var exponential = FirstRetry * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        var delay = exponential < MaxRetry ? exponential : MaxRetry;
        return delay * (0.8 + (RandomNumberGenerator.GetInt32(401) / 1000.0));
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
