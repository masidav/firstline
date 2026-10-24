using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// The selection draw (migration 0017), run by staff once registration has closed: fans whose hub cities include the
/// show's city first, then everyone else, each ranked by their registration-time raw score, ties ordered by a stored
/// random seed. <c>run_allocation_selection()</c> does the work and records the draw once.
/// </summary>
public static partial class AllocationEndpoints
{
    private static void MapSelection(RouteGroupBuilder admin)
    {
        admin.MapPost("/allocations/{allocationId:guid}/selection", RunSelectionAsync);
        admin.MapGet("/allocations/{allocationId:guid}/registrations", ListRegistrationsForStaffAsync);
    }

    /// <summary>The recorded draw. <c>Seed</c> and the registrations' snapshots are enough to repeat it.</summary>
    public sealed record Selection(
        Guid AllocationId, string Seed, int Tickets, int Registrations, int SelectedFans, int SeatsAssigned, DateTime RanAt);

    public sealed record StaffRegistration(string Handle, Registration Registration);

    public sealed record StaffRegistrations(Selection? Selection, IReadOnlyList<StaffRegistration> Items);

    // 201 when this call ran the draw, 200 when it had already run (the same result).
    private static async Task<IResult> RunSelectionAsync(
        ClaimsPrincipal user, Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var before = await LoadAllocationAsync(connection, allocationId);
        if (before.Status == "draft")
        {
            throw Conflict("A draft has no registrations. Publish it first.");
        }

        Selection selection;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                selection = await connection.QuerySingleAsync<Selection>(
                    $"SELECT {SelectionColumns} FROM run_allocation_selection(@allocationId, @userId) sel",
                    new { allocationId, userId = user.UserId() }, transaction);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText.StartsWith("selection blocked: ", StringComparison.Ordinal))
            {
                throw e.MessageText["selection blocked: ".Length..] switch
                {
                    "registration_open" => new ApiProblemException(StatusCodes.Status409Conflict, "selection_not_ready",
                        "Registration is still open", "The draw runs after registration closes."),
                    "cancelled" => Conflict("This allocation was cancelled."),
                    "artist_not_active" => new ApiProblemException(StatusCodes.Status409Conflict, "artist_not_active",
                        "Artist is not active", "The artist isn't active, so the draw can't run."),
                    _ => NotFound("No allocation has this id."),
                };
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return before.Status == "selected"
            ? Results.Ok(selection)
            : Results.Created($"/v1/admin/allocations/{allocationId}/registrations", selection);
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
