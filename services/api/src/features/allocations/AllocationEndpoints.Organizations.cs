using Dapper;
using FandomScore.Api.Features.Organizations;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// A promoter organization's shows and allocations (migration 0037), under <c>/organizations/{organizationId}</c>: the
/// same handlers staff use under <c>/admin</c>, behind <see cref="OrganizationAccess"/> (members only, each route's
/// show or allocation must be the organization's). Admins and owners manage; analysts read. Promoters see fans as
/// handles and registration status only (<see cref="PromoterRegistration"/>): no scores or hub cities.
/// </summary>
public static partial class AllocationEndpoints
{
    private static RouteGroupBuilder MapOrganizationShows(RouteGroupBuilder v1)
    {
        var organization = v1.MapGroup("/organizations/{organizationId:guid}");
        var read = OrganizationAccess.Require("analyst");
        var manage = OrganizationAccess.Require("admin");

        organization.MapGet("/events", ListOrganizationEventsAsync).AddEndpointFilter(read);
        organization.MapPost("/events", CreateEventAsync).AddEndpointFilter(manage);
        organization.MapGet("/events/{eventId:guid}", GetEventForStaffAsync).AddEndpointFilter(read);
        organization.MapPatch("/events/{eventId:guid}", UpdateEventAsync).AddEndpointFilter(manage);
        organization.MapPost("/events/{eventId:guid}/allocations", CreateAllocationAsync).AddEndpointFilter(manage);
        organization.MapPatch("/allocations/{allocationId:guid}", UpdateAllocationAsync).AddEndpointFilter(manage);
        organization.MapPost("/allocations/{allocationId:guid}/publish", PublishAsync).AddEndpointFilter(manage);
        organization.MapPost("/allocations/{allocationId:guid}/cancel", CancelAsync).AddEndpointFilter(manage);
        organization.MapDelete("/allocations/{allocationId:guid}", DeleteDraftAsync).AddEndpointFilter(manage);
        organization.MapPost("/allocations/{allocationId:guid}/selection", QueueSelectionAsync).AddEndpointFilter(manage);
        organization.MapGet("/allocations/{allocationId:guid}/selection", GetSelectionAsync).AddEndpointFilter(read);
        organization.MapGet("/allocations/{allocationId:guid}/registrations", ListRegistrationsForOrganizationAsync).AddEndpointFilter(read);
        organization.MapGet("/allocations/{allocationId:guid}/reservations", ListReservationsForStaffAsync).AddEndpointFilter(read);
        return organization;
    }

    public sealed record OrganizationEvents(IReadOnlyList<EventDetail> Items);

    /// <summary>
    /// openapi PromoterRegistration: what a promoter sees of a registration. <c>Local</c>: the show's city is one of
    /// the fan's hub cities (the draw puts local fans first).
    /// </summary>
    public sealed record PromoterRegistration(
        string Handle, string Status, bool Local, bool PlusOne, int? SelectionRank, int? Seats, DateTime RegisteredAt, DateTime? WithdrawnAt);

    public sealed record PromoterRegistrations(Selection? Selection, IReadOnlyList<PromoterRegistration> Items);

    // Newest show first, drafts included, at most 200.
    private static async Task<IResult> ListOrganizationEventsAsync(Guid organizationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(
            "SELECT id FROM events WHERE organization_id = @organizationId ORDER BY starts_at DESC LIMIT 200", new { organizationId });
        var items = new List<EventDetail>();
        foreach (var id in ids)
        {
            items.Add(await LoadEventAsync(connection, id, includeDrafts: true));
        }

        return Results.Ok(new OrganizationEvents(items));
    }

    // Same order as staff see (ranking once the draw has run), reduced to what a promoter may see.
    private static async Task<IResult> ListRegistrationsForOrganizationAsync(
        Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
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
        return Results.Ok(new PromoterRegistrations(selection, [.. rows.Select(row =>
        {
            var r = ToRegistration(row);
            return new PromoterRegistration(row.Handle, r.Status, r.Local, r.PlusOne, r.SelectionRank, r.Seats, r.RegisteredAt, r.WithdrawnAt);
        })]));
    }
}
