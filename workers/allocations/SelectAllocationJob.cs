using System.Text.Json;
using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.Allocations;

/// <summary>
/// Runs an allocation's selection draw (<c>run_allocation_selection()</c>, migration 0017), queued by staff through
/// <c>POST /v1/admin/allocations/{id}/selection</c>. The draw is linear in the registrations, so it runs here rather
/// than inside the request; its result doesn't depend on when it runs (snapshots and a stored seed fix the ranking).
/// Idempotent: a second run returns the recorded draw. A cancelled allocation ends the job without a draw; any other
/// refusal (registration reopened can't happen, but an artist paused meanwhile can) fails the attempt and is retried.
/// </summary>
public sealed partial class SelectAllocationJob(NpgsqlDataSource db, ILogger<SelectAllocationJob> logger) : IJobHandler
{
    public const string JobKind = "allocations.select";

    public string Kind => JobKind;

    public TimeSpan? Every => null;

    /// <summary>What the API enqueues: the allocation, and the staff member who asked.</summary>
    public sealed record Payload(Guid AllocationId, Guid? RanBy);

    public static string DedupeKey(Guid allocationId) => $"{JobKind}:{allocationId}";

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.Payload, JsonOptions)
                      ?? throw new InvalidOperationException("The job has no payload.");
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        try
        {
            var result = await connection.QuerySingleAsync<(int Registrations, int SelectedFans, int SeatsAssigned, int Tickets)>(
                "SELECT registrations, selected_fans, seats_assigned, tickets FROM run_allocation_selection(@allocationId, @ranBy)",
                new { payload.AllocationId, payload.RanBy });
            LogDrawn(logger, payload.AllocationId, result.Registrations, result.SelectedFans, result.SeatsAssigned, result.Tickets);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText == "selection blocked: cancelled")
        {
            LogCancelled(logger, payload.AllocationId);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Draw for allocation {AllocationId}: {Registrations} registrations, {SelectedFans} fans selected, {SeatsAssigned}/{Tickets} seats.")]
    private static partial void LogDrawn(ILogger logger, Guid allocationId, int registrations, int selectedFans, int seatsAssigned, int tickets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Allocation {AllocationId} was cancelled before its draw; nothing to do.")]
    private static partial void LogCancelled(ILogger logger, Guid allocationId);
}
