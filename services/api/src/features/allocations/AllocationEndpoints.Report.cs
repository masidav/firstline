using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Organizations;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// The pilot case-study report for a show (PROJECT_TODO.md Phase 11): per allocation and rolled up, how many seats were
/// offered, who registered, who was selected, what was delivered, returned, re-offered, and how many seats stayed
/// empty. Everything is computed from registrations, the recorded draw, reservations, and the eligibility audit;
/// only scalping complaints are entered by hand (migration 0047), because the platform never sees a resale.
/// Promoter members read their own shows' reports; staff read any show's.
/// </summary>
public static partial class AllocationEndpoints
{
    private static void MapReports(RouteGroupBuilder admin, RouteGroupBuilder organization)
    {
        admin.MapGet("/events/{eventId:guid}/report", GetEventReportAsync);
        admin.MapPut("/allocations/{allocationId:guid}/complaints", PutComplaintsAsync);
        organization.MapGet("/events/{eventId:guid}/report", GetEventReportAsync).AddEndpointFilter(OrganizationAccess.Require("analyst"));
        organization.MapPut("/allocations/{allocationId:guid}/complaints", PutComplaintsAsync).AddEndpointFilter(OrganizationAccess.Require("admin"));
    }

    /// <summary>
    /// openapi ReportMetrics. Seats, not fans, unless the name says fans: a +1 is two seats on one reservation.
    /// <c>EmptySeats</c> is set once the show has started (seats nobody held at show start); before that
    /// <c>SeatsUnfilled</c> is how many are free right now. <c>ScalpingComplaints</c> is null until someone records them.
    /// </summary>
    public sealed record ReportMetrics(
        int SeatsOffered, int Registrations, int Withdrawn, int EligibleRegistrations, int IneligibleAttempts, int HubRegistrations,
        int NonHubRegistrations, int SelectedFans, int SelectedInDraw, int PlusOnes, int SeatsFilled, int HubSeatsFilled,
        int NonHubSeatsFilled, int TicketsDelivered, int Returns, int SeatsReturned, int Cancellations, int ReOffers,
        int SeatsReOffered, int SeatsUnfilled, int? EmptySeats, decimal? SellThroughPercent, int? ScalpingComplaints);

    /// <summary>openapi AllocationComplaints: the hand-entered scalping complaints for one allocation.</summary>
    public sealed record AllocationComplaints(Guid AllocationId, int ScalpingComplaints, string? Note, string UpdatedByHandle, DateTime UpdatedAt);

    public sealed record AllocationReport(Allocation Allocation, ReportMetrics Metrics, AllocationComplaints? Complaints);

    /// <summary>openapi ShowReport: the show, every published allocation, and the roll-up of those not cancelled.</summary>
    public sealed record ShowReport(Show Event, DateTime GeneratedAt, bool ShowStarted, ReportMetrics Totals, IReadOnlyList<AllocationReport> Allocations);

    private static async Task<IResult> GetEventReportAsync(Guid eventId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var detail = await LoadEventAsync(connection, eventId, includeDrafts: true);
        var started = await connection.ExecuteScalarAsync<bool>("SELECT now() >= starts_at FROM events WHERE id = @eventId", new { eventId });
        var rows = (await connection.QueryAsync<MetricsRow>(MetricsSql, new { eventId })).ToDictionary(r => r.AllocationId);
        var complaints = (await connection.QueryAsync<AllocationComplaints>(
                """
                SELECT c.allocation_id AS AllocationId, c.scalping_complaints AS ScalpingComplaints, c.note AS Note,
                       u.handle::text AS UpdatedByHandle, c.updated_at AS UpdatedAt
                FROM allocation_complaints c JOIN allocations a ON a.id = c.allocation_id JOIN users u ON u.id = c.updated_by
                WHERE a.event_id = @eventId
                """,
                new { eventId }))
            .ToDictionary(c => c.AllocationId);

        var allocations = detail.Allocations.Where(a => a.Status != "draft").Select(a =>
        {
            var row = rows.GetValueOrDefault(a.Id) ?? new MetricsRow { AllocationId = a.Id };
            complaints.TryGetValue(a.Id, out var complaint);
            return new AllocationReport(a, ToMetrics(a.Tickets, row, started, complaint?.ScalpingComplaints), complaint);
        }).ToList();
        var counted = allocations.Where(a => a.Allocation.Status != "cancelled").ToList();
        var totals = ToMetrics(
            counted.Sum(a => a.Allocation.Tickets),
            counted.Aggregate(new MetricsRow(), (sum, a) => sum.Add(rows.GetValueOrDefault(a.Allocation.Id))),
            started,
            counted.Any(a => a.Complaints is not null) ? counted.Sum(a => a.Complaints?.ScalpingComplaints ?? 0) : null);
        return Results.Ok(new ShowReport(detail.Event, DateTime.UtcNow, started, totals, allocations));
    }

    public sealed record ComplaintsRequest(int ScalpingComplaints, string? Note);

    // Upsert; the latest entry wins. Allowed on any published allocation, before or after the show.
    private static async Task<IResult> PutComplaintsAsync(
        Guid allocationId, JsonElement body, ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var input = new BodyReader(body);
        var count = input.Integer("scalping_complaints", 0, 1_000_000, required: true);
        var note = input.Text("note", 2000, required: false, nullable: true).Value;
        input.ThrowIfInvalid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var allocation = await LoadAllocationAsync(connection, allocationId);
        if (allocation.Status == "draft")
        {
            throw ApiProblemException.Validation(new FieldError("scalping_complaints", "Publish the allocation before recording complaints."));
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO allocation_complaints (allocation_id, scalping_complaints, note, updated_by)
            VALUES (@allocationId, @count, @note, @userId)
            ON CONFLICT (allocation_id) DO UPDATE SET scalping_complaints = EXCLUDED.scalping_complaints, note = EXCLUDED.note,
                                                      updated_by = EXCLUDED.updated_by
            """,
            new { allocationId, count, note, userId = user.UserId() });
        var saved = await connection.QuerySingleAsync<AllocationComplaints>(
            """
            SELECT c.allocation_id AS AllocationId, c.scalping_complaints AS ScalpingComplaints, c.note AS Note,
                   u.handle::text AS UpdatedByHandle, c.updated_at AS UpdatedAt
            FROM allocation_complaints c JOIN users u ON u.id = c.updated_by WHERE c.allocation_id = @allocationId
            """,
            new { allocationId });
        return Results.Ok(saved);
    }

    private static ReportMetrics ToMetrics(int seatsOffered, MetricsRow r, bool started, int? complaints)
    {
        var unfilled = Math.Max(0, seatsOffered - r.SeatsFilled);
        return new ReportMetrics(seatsOffered, r.Registrations, r.Withdrawn, r.Registrations, r.IneligibleAttempts, r.HubRegistrations,
            r.Registrations - r.HubRegistrations, r.SelectedFans, r.SelectedInDraw, r.PlusOnes, r.SeatsFilled, r.HubSeatsFilled,
            r.SeatsFilled - r.HubSeatsFilled, r.TicketsDelivered, r.Returns, r.SeatsReturned, r.Cancellations, r.ReOffers, r.SeatsReOffered,
            unfilled, started ? unfilled : null,
            seatsOffered == 0 ? null : Math.Round(100m * r.SeatsFilled / seatsOffered, 1), complaints);
    }

    // One row per allocation of the show. Registrations: live ones (not withdrawn); every registration was eligible when
    // it was made (registration_blocker refuses the others), and the blocked attempts come from the eligibility audit
    // (migration 0042), one per fan (score, hub city, or a fraud hold). A re-offer is a reservation made after the draw (backfill after a return
    // or a cancellation). Delivered: live reservations past their delivery time.
    private const string MetricsSql =
        """
        SELECT a.id AS AllocationId,
               (SELECT count(*) FROM allocation_registrations r WHERE r.allocation_id = a.id AND r.withdrawn_at IS NULL)::int AS Registrations,
               (SELECT count(*) FROM allocation_registrations r WHERE r.allocation_id = a.id AND r.withdrawn_at IS NOT NULL)::int AS Withdrawn,
               (SELECT count(DISTINCT ea.user_id) FROM eligibility_audit ea
                 WHERE ea.allocation_id = a.id AND ea.action = 'registration_blocked'
                   AND ea.reason IN ('no_score', 'below_threshold', 'hub_city_required', 'under_review', 'unavailable'))::int AS IneligibleAttempts,
               (SELECT count(*) FROM allocation_registrations r
                 WHERE r.allocation_id = a.id AND r.withdrawn_at IS NULL AND r.hub_match IS NOT NULL)::int AS HubRegistrations,
               (SELECT count(*) FROM allocation_registrations r WHERE r.allocation_id = a.id AND r.outcome = 'selected')::int AS SelectedFans,
               COALESCE((SELECT sel.selected_fans FROM allocation_selections sel WHERE sel.allocation_id = a.id), 0) AS SelectedInDraw,
               (SELECT count(*) FROM reservations res WHERE res.allocation_id = a.id AND res.seats = 2
                   AND res.returned_at IS NULL AND res.cancelled_at IS NULL)::int AS PlusOnes,
               COALESCE((SELECT sum(res.seats) FROM reservations res WHERE res.allocation_id = a.id
                   AND res.returned_at IS NULL AND res.cancelled_at IS NULL), 0)::int AS SeatsFilled,
               COALESCE((SELECT sum(res.seats) FROM reservations res JOIN allocation_registrations r ON r.id = res.registration_id
                 WHERE res.allocation_id = a.id AND res.returned_at IS NULL AND res.cancelled_at IS NULL AND r.hub_match IS NOT NULL), 0)::int AS HubSeatsFilled,
               COALESCE((SELECT sum(res.seats) FROM reservations res WHERE res.allocation_id = a.id
                   AND res.returned_at IS NULL AND res.cancelled_at IS NULL AND now() >= res.deliver_at), 0)::int AS TicketsDelivered,
               (SELECT count(*) FROM reservations res WHERE res.allocation_id = a.id AND res.returned_at IS NOT NULL)::int AS Returns,
               COALESCE((SELECT sum(res.seats) FROM reservations res WHERE res.allocation_id = a.id AND res.returned_at IS NOT NULL), 0)::int AS SeatsReturned,
               (SELECT count(*) FROM reservations res WHERE res.allocation_id = a.id AND res.cancelled_at IS NOT NULL)::int AS Cancellations,
               (SELECT count(*) FROM reservations res JOIN allocation_selections sel ON sel.allocation_id = res.allocation_id
                 WHERE res.allocation_id = a.id AND res.created_at > sel.ran_at)::int AS ReOffers,
               COALESCE((SELECT sum(res.seats) FROM reservations res JOIN allocation_selections sel ON sel.allocation_id = res.allocation_id
                 WHERE res.allocation_id = a.id AND res.created_at > sel.ran_at), 0)::int AS SeatsReOffered
        FROM allocations a
        WHERE a.event_id = @eventId
        """;

    private sealed class MetricsRow
    {
        public Guid AllocationId { get; set; }
        public int Registrations { get; set; }
        public int Withdrawn { get; set; }
        public int IneligibleAttempts { get; set; }
        public int HubRegistrations { get; set; }
        public int SelectedFans { get; set; }
        public int SelectedInDraw { get; set; }
        public int PlusOnes { get; set; }
        public int SeatsFilled { get; set; }
        public int HubSeatsFilled { get; set; }
        public int TicketsDelivered { get; set; }
        public int Returns { get; set; }
        public int SeatsReturned { get; set; }
        public int Cancellations { get; set; }
        public int ReOffers { get; set; }
        public int SeatsReOffered { get; set; }

        public MetricsRow Add(MetricsRow? other)
        {
            if (other is null)
            {
                return this;
            }

            Registrations += other.Registrations;
            Withdrawn += other.Withdrawn;
            IneligibleAttempts += other.IneligibleAttempts;
            HubRegistrations += other.HubRegistrations;
            SelectedFans += other.SelectedFans;
            SelectedInDraw += other.SelectedInDraw;
            PlusOnes += other.PlusOnes;
            SeatsFilled += other.SeatsFilled;
            HubSeatsFilled += other.HubSeatsFilled;
            TicketsDelivered += other.TicketsDelivered;
            Returns += other.Returns;
            SeatsReturned += other.SeatsReturned;
            Cancellations += other.Cancellations;
            ReOffers += other.ReOffers;
            SeatsReOffered += other.SeatsReOffered;
            return this;
        }
    }
}
