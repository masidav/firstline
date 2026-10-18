namespace FandomScore.Worker.Jobs;

/// <summary>Deletes finished jobs after <see cref="JobOptions.KeepSucceeded"/> / <see cref="JobOptions.KeepFailed"/>.</summary>
public sealed class PruneJobsJob(JobQueue queue) : IJobHandler
{
    public string Kind => "jobs.prune";

    public TimeSpan? Every => TimeSpan.FromDays(1);

    public Task RunAsync(ClaimedJob job, CancellationToken cancellationToken) => queue.PruneAsync(cancellationToken);
}
