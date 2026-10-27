using System.Collections.Concurrent;
using Dapper;
using FandomScore.Database;
using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The Postgres job queue and runner, and the deleted-account purge job, against a real database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class JobQueueTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private JobQueue _queue = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"jobs_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _queue = NewQueue();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_job_runs_once_and_succeeds()
    {
        var handler = new RecordingHandler();
        var id = await _queue.EnqueueAsync("test.record", new { artist = "a-1" });

        Assert.True(await Runner(handler).RunOnceAsync(CancellationToken.None));
        Assert.False(await Runner(handler).RunOnceAsync(CancellationToken.None));

        Assert.Equal("""{"artist": "a-1"}""", Assert.Single(handler.Payloads));
        Assert.Equal("succeeded", await StatusAsync(id!.Value));
    }

    [Fact]
    public async Task Future_jobs_wait_and_duplicates_are_skipped()
    {
        Assert.NotNull(await _queue.EnqueueAsync("test.record", dedupeKey: "artist:1", runAt: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Null(await _queue.EnqueueAsync("test.record", dedupeKey: "artist:1"));

        Assert.Null(await _queue.ClaimAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failures_retry_with_backoff_then_give_up()
    {
        var id = (await _queue.EnqueueAsync("test.fail", maxAttempts: 2))!.Value;
        var runner = Runner(new FailingHandler());

        Assert.True(await runner.RunOnceAsync(CancellationToken.None));
        var first = await JobAsync(id);
        Assert.Equal("queued", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.True(first.RetryInSeconds is > 20 and < 40, $"retry in {first.RetryInSeconds}s");
        Assert.Equal("InvalidOperationException: Spotify said no", first.LastError);

        await ExecuteAsync("UPDATE jobs SET run_at = now() WHERE id = @id", new { id });
        Assert.True(await runner.RunOnceAsync(CancellationToken.None));
        Assert.Equal("failed", await StatusAsync(id));
    }

    [Fact]
    public void Retry_delay_doubles_up_to_an_hour()
    {
        Assert.InRange(JobQueue.RetryDelay(1).TotalSeconds, 24, 36);
        Assert.InRange(JobQueue.RetryDelay(3).TotalSeconds, 96, 144);
        Assert.InRange(JobQueue.RetryDelay(30).TotalMinutes, 48, 72);
    }

    [Fact]
    public async Task Unknown_kinds_fail_without_retrying()
    {
        var id = (await _queue.EnqueueAsync("test.nobody-handles-this"))!.Value;

        Assert.True(await Runner(new RecordingHandler()).RunOnceAsync(CancellationToken.None));

        Assert.Equal("failed", await StatusAsync(id));
    }

    [Fact]
    public async Task A_job_whose_worker_died_is_claimed_again()
    {
        var id = (await _queue.EnqueueAsync("test.record"))!.Value;
        Assert.NotNull(await _queue.ClaimAsync(CancellationToken.None));
        Assert.Null(await NewQueue().ClaimAsync(CancellationToken.None));

        await ExecuteAsync("UPDATE jobs SET locked_until = now() - interval '1 second' WHERE id = @id", new { id });
        var reclaimed = await NewQueue().ClaimAsync(CancellationToken.None);

        Assert.Equal(id, reclaimed?.Id);
        Assert.Equal(2, reclaimed?.Attempts);
    }

    [Fact]
    public async Task Concurrent_workers_never_get_the_same_job()
    {
        for (var i = 0; i < 40; i++)
        {
            await _queue.EnqueueAsync("test.record");
        }

        var claimed = new ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var queue = NewQueue();
            while (await queue.ClaimAsync(CancellationToken.None) is { } job)
            {
                claimed.Add(job.Id);
            }
        })));

        Assert.Equal(40, claimed.Count);
        Assert.Equal(40, claimed.Distinct().Count());
    }

    [Fact]
    public async Task Schedules_fire_once_per_interval()
    {
        await _queue.RegisterScheduleAsync("test.record", TimeSpan.FromHours(1), CancellationToken.None);

        Assert.Equal(1, await _queue.FireDueSchedulesAsync(CancellationToken.None));
        Assert.Equal(0, await NewQueue().FireDueSchedulesAsync(CancellationToken.None));
        Assert.True(await Runner(new RecordingHandler()).RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Claims_skip_kinds_this_process_does_not_run()
    {
        var record = (await _queue.EnqueueAsync("test.record"))!.Value;
        var fail = (await _queue.EnqueueAsync("test.fail"))!.Value;
        var redaction = NewQueue(new JobOptions { OnlyKinds = "test.fail, test.other" });
        var worker = NewQueue(new JobOptions { ExceptKinds = "test.fail" });

        Assert.Equal(fail, (await redaction.ClaimAsync(CancellationToken.None))?.Id);
        Assert.Null(await redaction.ClaimAsync(CancellationToken.None));
        Assert.Equal(record, (await worker.ClaimAsync(CancellationToken.None))?.Id);
        Assert.Null(await worker.ClaimAsync(CancellationToken.None));

        // An abandoned job is reclaimed only by a process that runs its kind.
        await ExecuteAsync("UPDATE jobs SET locked_until = now() - interval '1 second'");
        Assert.Equal(record, (await worker.ClaimAsync(CancellationToken.None))?.Id);
        Assert.Null(await worker.ClaimAsync(CancellationToken.None));
        Assert.Equal(fail, (await redaction.ClaimAsync(CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task A_runner_registers_schedules_only_for_kinds_it_runs()
    {
        var options = Options.Create(new JobOptions { ExceptKinds = "test.elsewhere", PollInterval = TimeSpan.FromMilliseconds(50) });
        using var runner = new JobRunner(new JobQueue(_db, options), [new ScheduledHandler("test.elsewhere"), new ScheduledHandler("test.here")],
            options, TimeProvider.System, NullLogger<JobRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);
        try
        {
            // Schedules are registered before the loops start; the loop then fires and runs test.here.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await ScalarAsync<int>("SELECT count(*)::int FROM jobs WHERE kind = 'test.here' AND status = 'succeeded'") == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "test.here never ran");
                await Task.Delay(50);
            }
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }

        Assert.Equal(["test.here"], (await QueryAsync<string>("SELECT kind FROM job_schedules WHERE kind LIKE 'test.%' ORDER BY kind")).ToArray());
    }

    [Fact]
    public void Kind_filters_reject_typos_and_mixed_settings()
    {
        IJobHandler[] handlers = [new RecordingHandler(), new FailingHandler()];

        Assert.Equal(["test.record"], JobRunner.Select(handlers, JobKindFilter.Parse("test.record", null)).Keys);
        Assert.Equal(["test.fail"], JobRunner.Select(handlers, JobKindFilter.Parse(null, "test.record")).Keys);
        Assert.Equal(2, JobRunner.Select(handlers, JobKindFilter.Parse("", " ")).Count);
        Assert.Throws<InvalidOperationException>(() => JobRunner.Select(handlers, JobKindFilter.Parse("test.recrod", null)));
        Assert.Throws<InvalidOperationException>(() => JobKindFilter.Parse("test.record", "test.fail"));
    }

    [Fact]
    public async Task Deleted_accounts_are_purged_after_30_days_only()
    {
        await ExecuteAsync(
            """
            INSERT INTO users (id, handle, email) VALUES
              ('00000000-0000-0000-0000-0000000000a1', 'old_one', 'old@example.test'),
              ('00000000-0000-0000-0000-0000000000a2', 'recent', 'recent@example.test'),
              ('00000000-0000-0000-0000-0000000000a3', 'live', 'live@example.test');
            UPDATE users SET deleted_at = now() - interval '31 days' WHERE handle = 'old_one';
            UPDATE users SET deleted_at = now() - interval '5 days' WHERE handle = 'recent';
            INSERT INTO lists (user_id, title) VALUES
              ('00000000-0000-0000-0000-0000000000a1', 'Old'),
              ('00000000-0000-0000-0000-0000000000a2', 'Recent'),
              ('00000000-0000-0000-0000-0000000000a3', 'Live');
            """);
        var purge = new PurgeDeletedAccountsJob(_db, NullLogger<PurgeDeletedAccountsJob>.Instance);
        await _queue.RegisterScheduleAsync(purge.Kind, purge.Every!.Value, CancellationToken.None);
        await _queue.FireDueSchedulesAsync(CancellationToken.None);

        Assert.True(await Runner(purge).RunOnceAsync(CancellationToken.None));

        await using var connection = await _db.OpenConnectionAsync();
        var lists = (await connection.QueryAsync<string>("SELECT title FROM lists ORDER BY title")).ToList();
        Assert.Equal(["Live", "Recent"], lists);
        var purged = await connection.QueryAsync<Guid>("SELECT id FROM users WHERE content_purged_at IS NOT NULL");
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Assert.Single(purged));
    }

    private JobQueue NewQueue(JobOptions? options = null) => new(_db, Options.Create(options ?? new JobOptions()));

    private JobRunner Runner(IJobHandler handler) =>
        new(_queue, [handler], Options.Create(new JobOptions()), TimeProvider.System, NullLogger<JobRunner>.Instance);

    private async Task<string> StatusAsync(long id) => (await JobAsync(id)).Status;

    private async Task<JobRow> JobAsync(long id)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<JobRow>(
            """
            SELECT status AS Status, attempts AS Attempts, last_error AS LastError,
                   extract(epoch FROM run_at - now())::float8 AS RetryInSeconds
            FROM jobs WHERE id = @id
            """,
            new { id });
    }

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException("No value.");
    }

    private async Task<IEnumerable<T>> QueryAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QueryAsync<T>(sql);
    }

    public sealed record JobRow(string Status, int Attempts, string? LastError, double RetryInSeconds);

    private sealed class RecordingHandler : IJobHandler
    {
        public ConcurrentQueue<string> Payloads { get; } = new();

        public string Kind => "test.record";

        public Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
        {
            Payloads.Enqueue(job.Payload);
            return Task.CompletedTask;
        }
    }

    private sealed class ScheduledHandler(string kind) : IJobHandler
    {
        public string Kind => kind;

        public TimeSpan? Every => TimeSpan.FromHours(1);

        public Task RunAsync(ClaimedJob job, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingHandler : IJobHandler
    {
        public string Kind => "test.fail";

        public Task RunAsync(ClaimedJob job, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Spotify said no");
    }
}
