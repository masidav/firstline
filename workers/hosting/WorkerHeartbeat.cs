using System.Collections.Concurrent;

namespace FandomScore.Worker.Hosting;

/// <summary>Liveness endpoint settings (<c>Health</c> section). Unset port: no endpoint, as in local development.</summary>
public sealed class HealthOptions
{
    public const string Section = "Health";

    /// <summary>TCP port of the plain-HTTP liveness endpoint; 0 disables it.</summary>
    public int Port { get; set; }

    /// <summary>
    /// A job loop that has not reported in this long is stuck. Handlers must finish within <c>Jobs__Lease</c>
    /// (5 minutes by default), so twice that leaves room for a slow job without restarting a healthy pod.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>What the liveness endpoint reports.</summary>
public sealed record HealthReport(bool Alive, DateTimeOffset StartedAt, IReadOnlyDictionary<int, DateTimeOffset> Loops);

/// <summary>
/// Each job loop in <see cref="Jobs.JobRunner"/> reports here once per iteration (before polling the queue and
/// after a job). The worker serves no HTTP, so this is what Kubernetes' liveness probe looks at
/// (infrastructure/k8s/base/worker-deployment.yaml): a loop that stops reporting is stuck, and the pod restarts.
/// A loop that keeps failing to reach the database still reports; restarting would not help with that, and the
/// errors are in the logs and the alerts (infrastructure/terraform/modules/environment/monitoring.tf).
/// </summary>
public sealed class WorkerHeartbeat(TimeProvider time)
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _loops = new();

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public void Beat(int loop) => _loops[loop] = time.GetUtcNow();

    /// <summary>Alive while every loop that ever reported did so within <paramref name="staleAfter"/>, or
    /// nothing has reported yet and the process started less than that ago.</summary>
    public HealthReport Report(TimeSpan staleAfter)
    {
        var loops = _loops.ToDictionary();
        var oldest = loops.Count == 0 ? StartedAt : loops.Values.Min();
        return new HealthReport(time.GetUtcNow() - oldest <= staleAfter, StartedAt, loops);
    }
}
