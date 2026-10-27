using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// The selection draw (migration 0017), asked for by staff once registration has closed: fans whose hub cities
/// include the show's city first, then everyone else, each ranked by their registration-time raw score, ties
/// ordered by a stored random seed. The draw itself runs in the worker (<c>allocations.select</c>, migration 0036):
/// it is linear in the registrations, and nothing depends on how soon it runs. Staff queue it here and read its
/// state from <c>GET .../selection</c>.
/// </summary>
public static partial class AllocationEndpoints
{
    /// <summary>The worker job that runs the draw (workers/allocations/SelectAllocationJob.cs).</summary>
    public const string SelectionJobKind = "allocations.select";

    private static void MapSelection(RouteGroupBuilder admin)
    {
        admin.MapPost("/allocations/{allocationId:guid}/selection", QueueSelectionAsync);
        admin.MapGet("/allocations/{allocationId:guid}/selection", GetSelectionAsync);
        admin.MapGet("/allocations/{allocationId:guid}/registrations", ListRegistrationsForStaffAsync);
    }

    /// <summary>The recorded draw. <c>Seed</c> and the registrations' snapshots are enough to repeat it.</summary>
    public sealed record Selection(
        Guid AllocationId, string Seed, int Tickets, int Registrations, int SelectedFans, int SeatsAssigned, DateTime RanAt);

    /// <summary>
    /// openapi DrawStatus: where the draw stands. <c>Status</c> is not_run, queued, running, failed (the worker gave
    /// up; <c>Error</c> says why, and queuing again retries), or done (<c>Selection</c> is the recorded draw).
    /// </summary>
    public sealed record DrawStatus(Guid AllocationId, string Status, DateTime? QueuedAt, string? Error, Selection? Selection);

    public sealed record StaffRegistration(string Handle, Registration Registration);

    public sealed record StaffRegistrations(Selection? Selection, IReadOnlyList<StaffRegistration> Items);

    // 202 when the draw is queued (or already queued or running), 200 when it has run.
    private static async Task<IResult> QueueSelectionAsync(
        ClaimsPrincipal user, Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var allocation = await LoadAllocationAsync(connection, allocationId);
        if (allocation.Status == "draft")
        {
            throw Conflict("A draft has no registrations. Publish it first.");
        }

        var blocker = await connection.ExecuteScalarAsync<string?>(
            "SELECT allocation_selection_blocker(@allocationId)", new { allocationId });
        if (blocker is not null)
        {
            throw blocker switch
            {
                "registration_open" => new ApiProblemException(StatusCodes.Status409Conflict, "selection_not_ready",
                    "Registration is still open", "The draw runs after registration closes."),
                "cancelled" => Conflict("This allocation was cancelled."),
                "artist_not_active" => new ApiProblemException(StatusCodes.Status409Conflict, "artist_not_active",
                    "Artist is not active", "The artist isn't active, so the draw can't run."),
                _ => NotFound("No allocation has this id."),
            };
        }

        var status = await LoadDrawStatusAsync(connection, allocationId);
        if (status.Status == "done")
        {
            return Results.Ok(status);
        }

        if (status.Status is "not_run" or "failed")
        {
            // One queued draw per allocation (dedupe key); a failed one is queued again.
            await connection.ExecuteAsync(
                "SELECT enqueue_job(@kind, @payload::jsonb, @dedupeKey)",
                new
                {
                    kind = SelectionJobKind,
                    payload = JsonSerializer.Serialize(new { allocation_id = allocationId, ran_by = user.UserId() }),
                    dedupeKey = $"{SelectionJobKind}:{allocationId}",
                });
            status = await LoadDrawStatusAsync(connection, allocationId);
        }

        return Results.Accepted($"/v1/admin/allocations/{allocationId}/selection", status);
    }

    private static async Task<IResult> GetSelectionAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var allocation = await LoadAllocationAsync(connection, allocationId);
        if (allocation.Status == "draft")
        {
            throw NotFound("No allocation has this id.");
        }

        return Results.Ok(await LoadDrawStatusAsync(connection, allocationId));
    }

    // The recorded draw wins; otherwise the newest job for the allocation says whether it is queued, running, or failed.
    private static async Task<DrawStatus> LoadDrawStatusAsync(NpgsqlConnection connection, Guid allocationId)
    {
        var selection = await connection.QuerySingleOrDefaultAsync<Selection>(
            $"SELECT {SelectionColumns} FROM allocation_selections sel WHERE sel.allocation_id = @allocationId", new { allocationId });
        if (selection is not null)
        {
            return new DrawStatus(allocationId, "done", null, null, selection);
        }

        var job = await connection.QuerySingleOrDefaultAsync<(string Status, DateTime CreatedAt, string? LastError)>(
            """
            SELECT status, created_at, last_error FROM jobs
            WHERE kind = @kind AND payload->>'allocation_id' = @allocationId::text
            ORDER BY created_at DESC LIMIT 1
            """,
            new { kind = SelectionJobKind, allocationId });
        return job.Status switch
        {
            "queued" => new DrawStatus(allocationId, "queued", job.CreatedAt, null, null),
            "running" => new DrawStatus(allocationId, "running", job.CreatedAt, null, null),
            "failed" => new DrawStatus(allocationId, "failed", job.CreatedAt, job.LastError, null),
            _ => new DrawStatus(allocationId, "not_run", null, null, null),
        };
    }

    // Live registrations in ranking order once the draw has run (registration order before), withdrawn ones last.
    private static async Task<IResult> ListRegistrationsForStaffAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await LoadAllocationAsync(connection, allocationId);
        var selection = await connection.QuerySingleOrDefaultAsync<Selection>(
            $"SELECT {SelectionColumns} FROM allocation_selections sel WHERE sel.allocation_id = @allocationId", new { allocationId });
        var rows = await connection.QueryAsync<StaffRegistrationRow>(
            $"""
             SELECT u.handle AS Handle, {RegistrationColumns}
             FROM {RegistrationSource} JOIN users u ON u.id = r.user_id
             WHERE r.allocation_id = @allocationId
             ORDER BY (r.withdrawn_at IS NOT NULL), r.selection_rank NULLS LAST, r.registered_at
             """,
            new { allocationId });
        return Results.Ok(new StaffRegistrations(selection, [.. rows.Select(row => new StaffRegistration(row.Handle, ToRegistration(row)))]));
    }

    private const string SelectionColumns =
        """
        sel.allocation_id AS AllocationId, sel.seed AS Seed, sel.tickets AS Tickets, sel.registrations AS Registrations,
        sel.selected_fans AS SelectedFans, sel.seats_assigned AS SeatsAssigned, sel.ran_at AS RanAt
        """;

    private sealed class StaffRegistrationRow : RegistrationRow
    {
        public string Handle { get; set; } = "";
    }
}
