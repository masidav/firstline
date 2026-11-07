using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Worker.Hosting;

/// <summary>How an outside service answered when probed.</summary>
public enum IntegrationState
{
    /// <summary>Not configured (no key); nothing uses it.</summary>
    Off,

    /// <summary>Configured and answering.</summary>
    Ok,

    /// <summary>Configured, but the service rejected our credentials (401 or 403): a configuration problem.</summary>
    Refused,

    /// <summary>Configured, but the service could not be reached or answered with an error; usually passes.</summary>
    Unavailable,
}

public sealed record IntegrationStatus(IntegrationState State, string? Detail = null)
{
    public static IntegrationStatus Off { get; } = new(IntegrationState.Off);

    public static IntegrationStatus Ok { get; } = new(IntegrationState.Ok);
}

/// <summary>
/// An outside service the worker calls, probed on demand by <c>GET /healthz/integrations</c> (<see cref="WorkerHealthServer"/>).
/// A check makes one cheap real request with the configured credentials, so a deploy can tell a working key from a
/// wrong one (infrastructure/k8s/deploy.sh, "Integrations"). It must never throw: map failures to a state.
/// </summary>
public interface IIntegrationCheck
{
    /// <summary>Lower-case name used as the JSON key, e.g. <c>ticketmaster</c>.</summary>
    string Name { get; }

    Task<IntegrationStatus> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Runs every registered <see cref="IIntegrationCheck"/> in a service scope (typed HTTP clients are scoped).</summary>
public sealed class IntegrationChecks(IServiceScopeFactory scopes)
{
    /// <summary>Longest a probe may take; the deploy script waits a little longer than this.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    public async Task<IReadOnlyDictionary<string, IntegrationStatus>> RunAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var checks = scope.ServiceProvider.GetServices<IIntegrationCheck>().ToList();
        var results = await Task.WhenAll(checks.Select(c => RunOneAsync(c, timeout.Token)));
        return checks.Zip(results).ToDictionary(pair => pair.First.Name, pair => pair.Second, StringComparer.Ordinal);
    }

    /// <summary>The worst state across the checks: a refused credential outranks an outage, which outranks ok and off.</summary>
    public static IntegrationState Overall(IEnumerable<IntegrationStatus> statuses)
    {
        var states = statuses.Select(s => s.State).ToList();
        if (states.Contains(IntegrationState.Refused))
        {
            return IntegrationState.Refused;
        }

        if (states.Contains(IntegrationState.Unavailable))
        {
            return IntegrationState.Unavailable;
        }

        return states.Contains(IntegrationState.Ok) ? IntegrationState.Ok : IntegrationState.Off;
    }

    private static async Task<IntegrationStatus> RunOneAsync(IIntegrationCheck check, CancellationToken cancellationToken)
    {
        try
        {
            return await check.CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new IntegrationStatus(IntegrationState.Unavailable, $"No answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }
}
