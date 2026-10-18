using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Jobs;

/// <summary>Runs one kind of job. Handlers must be idempotent: a job can run again after a crash.</summary>
public interface IJobHandler
{
    string Kind { get; }

    /// <summary>Set to run the job on a schedule (registered at startup).</summary>
    TimeSpan? Every => null;

    Task RunAsync(ClaimedJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Polls the queue and runs jobs with their handlers, <see cref="JobOptions.Concurrency"/> at a time.
/// One loop also fires due schedules.
/// </summary>
public sealed partial class JobRunner(
    JobQueue queue, IEnumerable<IJobHandler> handlers, IOptions<JobOptions> options, TimeProvider time,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Kind);
    private readonly JobOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var handler in _handlers.Values)
        {
            if (handler.Every is { } every)
            {
                await queue.RegisterScheduleAsync(handler.Kind, every, stoppingToken);
            }
        }

        LogStarted(logger, queue.WorkerId, _handlers.Count, _options.Concurrency);
        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.Concurrency))
            .Select(i => LoopAsync(firesSchedules: i == 0, stoppingToken)));
    }

    /// <summary>Claims and runs one job. Returns false when no job was ready.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var job = await queue.ClaimAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        if (!_handlers.TryGetValue(job.Kind, out var handler))
        {
            LogNoHandler(logger, job.Id, job.Kind);
            await queue.FailAsync(job, new InvalidOperationException($"No handler for job kind '{job.Kind}'."), permanent: true);
            return true;
        }

        if (job.Attempts > job.MaxAttempts)
        {
            // Its lease kept expiring: the job crashes or hangs the worker.
            await queue.FailAsync(job, new TimeoutException("The job's lease expired on every attempt."), permanent: true);
            return true;
        }

        try
        {
            await handler.RunAsync(job, cancellationToken);
            await queue.CompleteAsync(job.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await queue.ReleaseAsync(job.Id);
            throw;
        }
        catch (Exception e)
        {
            LogJobFailed(logger, e, job.Id, job.Kind, job.Attempts, job.MaxAttempts);
            await queue.FailAsync(job, e);
        }

        return true;
    }

    private async Task LoopAsync(bool firesSchedules, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (firesSchedules)
                {
                    await queue.FireDueSchedulesAsync(stoppingToken);
                }

                if (!await RunOnceAsync(stoppingToken))
                {
                    await Task.Delay(_options.PollInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Usually the database is unreachable; keep trying.
                LogLoopError(logger, e);
                await Task.Delay(_options.PollInterval, time, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {WorkerId} started: {Handlers} job kinds, concurrency {Concurrency}.")]
    private static partial void LogStarted(ILogger logger, string workerId, int handlers, int concurrency);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} has kind {Kind}, which no handler runs.")]
    private static partial void LogNoHandler(ILogger logger, long jobId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} ({Kind}) failed on attempt {Attempt} of {MaxAttempts}.")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, long jobId, string kind, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job loop error; retrying.")]
    private static partial void LogLoopError(ILogger logger, Exception exception);
}
