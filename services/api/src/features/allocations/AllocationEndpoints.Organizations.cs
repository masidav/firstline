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
        organization.MapGet("/allocations/{allocationId:guid}/eligibility-preview", GetEligibilityPreviewAsync).AddEndpointFilter(read);
        return organization;
    }

    /// <summary>
    /// openapi EligibilityPreview: how many fans could register under the allocation's rules, as counts only. <c>Threshold</c>
    /// is <c>min_raw_score</c>, or the value publishing would set (<c>ThresholdIsDefault</c>). <c>ScoredFans</c> have a
    /// score for the artist and could register (not deleted, not on fraud hold, with a hub city); <c>Excluded</c> have a
    /// score but couldn't. <c>Eligible</c> clear the threshold; <c>Alternatives</c> repeat it for other thresholds.
    /// </summary>
    public sealed record EligibilityPreview(
        Guid AllocationId, int Tickets, decimal Threshold, bool ThresholdIsDefault, int ScoredFans, int Excluded,
        EligibilityCount Eligible, IReadOnlyList<EligibilityCount> Alternatives, DateTime ComputedAt);

    /// <summary>openapi EligibilityCount: fans at or above <c>Threshold</c>; <c>Local</c> have the show's city among their hubs.</summary>
    public sealed record EligibilityCount(decimal Threshold, int Total, int Local, int Other);

    private sealed record PreviewRules(int Tickets, decimal Threshold, bool IsDefault, decimal DefaultThreshold);

    private sealed record PreviewPool(int Scored, int Excluded);

    private sealed record PreviewCount(decimal Threshold, int Total, int Local);

    // Aggregates only, never a fan: promoters judge the pool and the threshold, not who is in it. The pool is every fan
    // with a score for the artist who registration would let in today (registration_blocker(): not deleted, not on
    // hold, a hub city set); the artist's status and the registration window are left out on purpose, since the
    // preview is meant for before registration opens. One pass over user_artist_scores_rank_idx (artist_id, raw_score).
    private static async Task<IResult> GetEligibilityPreviewAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rules = await connection.QuerySingleOrDefaultAsync<PreviewRules>(
            """
            SELECT a.tickets AS Tickets,
                   COALESCE(a.min_raw_score, allocation_default_threshold(ev.artist_id)) AS Threshold,
                   a.min_raw_score IS NULL AS IsDefault,
                   allocation_default_threshold(ev.artist_id) AS DefaultThreshold
              FROM allocations a JOIN events ev ON ev.id = a.event_id
             WHERE a.id = @allocationId
            """,
            new { allocationId }) ?? throw NotFound("No allocation has this id.");

        // Alternatives around the threshold (half, double, four times); around the default when the threshold is 0.
        var step = rules.Threshold > 0 ? rules.Threshold : rules.DefaultThreshold;
        var thresholds = new[] { rules.Threshold, step / 2, step, step * 2, step * 4 }
            .Select(t => Math.Round(t, 2)).Distinct().Order().ToArray();

        var pool = await connection.QuerySingleAsync<PreviewPool>(
            $"""
             WITH fans AS ({PreviewFans})
             SELECT count(*) FILTER (WHERE eligible)::int AS Scored, count(*) FILTER (WHERE NOT eligible)::int AS Excluded FROM fans
             """,
            new { allocationId });
        var counts = (await connection.QueryAsync<PreviewCount>(
            $"""
             WITH fans AS ({PreviewFans})
             SELECT t.threshold AS Threshold,
                    count(f.raw_score) FILTER (WHERE f.raw_score >= t.threshold)::int AS Total,
                    count(f.raw_score) FILTER (WHERE f.raw_score >= t.threshold AND f.local)::int AS Local
               FROM unnest(@thresholds::numeric[]) AS t(threshold)
               LEFT JOIN fans f ON f.eligible
              GROUP BY t.threshold ORDER BY t.threshold
             """,
            new { allocationId, thresholds }))
            .Select(c => new EligibilityCount(c.Threshold, c.Total, c.Local, c.Total - c.Local)).ToList();

        return Results.Ok(new EligibilityPreview(allocationId, rules.Tickets, rules.Threshold, rules.IsDefault, pool.Scored, pool.Excluded,
            counts.Single(c => c.Threshold == rules.Threshold), counts, DateTime.UtcNow));
    }

    // Every fan with a score for the show's artist: eligible when registration would let them in, local when the
    // show's city is one of their hub cities.
    private const string PreviewFans =
        """
        SELECT s.raw_score,
               u.deleted_at IS NULL AND NOT fan_on_hold(s.user_id)
                 AND EXISTS (SELECT 1 FROM user_hub_cities h WHERE h.user_id = s.user_id) AS eligible,
               EXISTS (SELECT 1 FROM user_hub_cities h WHERE h.user_id = s.user_id AND h.city_id = ev.city_id) AS local
          FROM allocations a JOIN events ev ON ev.id = a.event_id
          JOIN user_artist_scores s ON s.artist_id = ev.artist_id
          JOIN users u ON u.id = s.user_id
         WHERE a.id = @allocationId
        """;

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
