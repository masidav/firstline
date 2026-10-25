using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// Reservations (migration 0020): what a selected fan holds. Non-transferable (the token is shown only to its holder,
/// and only once delivered, <c>delivery_hours_before_show</c> before the show), returnable until the show starts
/// (<c>return_reservation()</c> hands the seats to the next fans in the draw's ranking at the same face value).
/// </summary>
public static partial class AllocationEndpoints
{
    private static void MapReservations(RouteGroupBuilder v1, RouteGroupBuilder admin)
    {
        v1.MapGet("/me/reservations", ListMyReservationsAsync);
        v1.MapGet("/reservations/{reservationId:guid}", GetReservationAsync);
        v1.MapPost("/reservations/{reservationId:guid}/return", ReturnReservationAsync);
        admin.MapGet("/allocations/{allocationId:guid}/reservations", ListReservationsForStaffAsync);
    }

    /// <summary>The ticket itself: only the holder sees it, and only once delivered.</summary>
    public sealed record Ticket(string Token, string HolderHandle, string? GuestHandle);

    /// <summary>
    /// <c>Status</c>: <c>held</c> (a seat is yours; the ticket arrives at <c>DeliverAt</c>), <c>delivered</c>,
    /// <c>returned</c>, or <c>cancelled</c>. <c>Role</c>: <c>holder</c>, or <c>guest</c> (the holder's named +1).
    /// </summary>
    public sealed record Reservation(
        Guid Id, Guid AllocationId, Guid EventId, Guid RegistrationId, string Status, string Role, int Seats, int FaceValueCents,
        string Currency, DateTime DeliverAt, DateTime CreatedAt, DateTime? ReturnedAt, DateTime? CancelledAt, string? CancelledReason,
        string HolderHandle, string? GuestHandle, bool CanReturn, Ticket? Ticket, Show Event, ScoringEndpoints.ArtistRef Artist,
        string AllocationName);

    public sealed record Reservations(IReadOnlyList<Reservation> Items);

    public sealed record StaffReservation(string Handle, Reservation Reservation);

    public sealed record StaffReservations(IReadOnlyList<StaffReservation> Items);

    // The fan's own reservations and the ones they are the named guest of, newest first.
    private static async Task<IResult> ListMyReservationsAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReservationRow>(
            $"{ReservationSelect} WHERE (res.user_id = @me OR reg.guest_user_id = @me) ORDER BY res.created_at DESC LIMIT 100", new { me });
        return Results.Ok(new Reservations([.. rows.Select(r => ToReservation(r, me))]));
    }

    private static async Task<IResult> GetReservationAsync(
        ClaimsPrincipal user, Guid reservationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadReservationAsync(connection, me, reservationId));
    }

    // Face-value exchange: the holder gives the reservation back before the show; the seats go on to the next fans
    // in the ranking in the same transaction. Returning twice is fine.
    private static async Task<IResult> ReturnReservationAsync(
        ClaimsPrincipal user, Guid reservationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await connection.ExecuteAsync("SELECT return_reservation(@me, @reservationId)", new { me, reservationId }, transaction);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText.StartsWith("return blocked: ", StringComparison.Ordinal))
            {
                throw e.MessageText["return blocked: ".Length..] switch
                {
                    "cancelled" => ReservationUnavailable("This reservation was cancelled."),
                    "show_started" => ReservationUnavailable("The show has started, so the reservation can't be returned."),
                    _ => NotFound("You don't hold a reservation with this id."),
                };
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await LoadReservationAsync(connection, me, reservationId));
    }

    private static async Task<IResult> ListReservationsForStaffAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await LoadAllocationAsync(connection, allocationId);
        var rows = await connection.QueryAsync<ReservationRow>(
            $"{ReservationSelect} WHERE res.allocation_id = @allocationId ORDER BY reg.selection_rank, res.created_at", new { allocationId });
        // Staff see who holds what, not the tokens.
        return Results.Ok(new StaffReservations([.. rows.Select(r => new StaffReservation(r.HolderHandle, ToReservation(r, viewer: null)))]));
    }

    private static async Task<Reservation> LoadReservationAsync(NpgsqlConnection connection, Guid me, Guid reservationId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReservationRow>(
            $"{ReservationSelect} WHERE res.id = @reservationId AND (res.user_id = @me OR reg.guest_user_id = @me)", new { me, reservationId });
        return row is null ? throw NotFound("You don't hold a reservation with this id.") : ToReservation(row, me);
    }

    private const string ReservationSelect =
        $"""
         SELECT res.id AS Id, res.allocation_id AS AllocationId, res.event_id AS EventId, res.registration_id AS RegistrationId,
                res.user_id AS HolderId, {ReservationStatusSql} AS Status, res.seats::int AS Seats, res.face_value_cents AS FaceValueCents,
                res.currency AS Currency, res.deliver_at AS DeliverAt, res.created_at AS CreatedAt, res.returned_at AS ReturnedAt,
                res.cancelled_at AS CancelledAt, res.cancelled_reason AS CancelledReason, h.handle::text AS HolderHandle,
                g.handle::text AS GuestHandle, res.token AS Token, now() < e.starts_at AS BeforeShow, al.name AS AllocationName,
                ar.id AS ArtistId, ar.name AS ArtistName, ar.image_url AS ArtistImageUrl,
                e.name AS Name, e.venue AS Venue, e.city_id AS CityId, COALESCE(c.name, e.city) AS City, c.region AS Region,
                COALESCE(c.country, e.country) AS Country, e.starts_at AS StartsAt,
                EXISTS (SELECT 1 FROM allocations x WHERE x.event_id = e.id AND x.published_at IS NOT NULL
                          AND x.cancelled_at IS NULL AND x.registration_closes_at > now()) AS TicketsAvailable
         FROM reservations res
         JOIN allocation_registrations reg ON reg.id = res.registration_id
         JOIN allocations al ON al.id = res.allocation_id
         JOIN events e ON e.id = res.event_id LEFT JOIN cities c ON c.id = e.city_id
         JOIN artists ar ON ar.id = e.artist_id
         JOIN users h ON h.id = res.user_id
         LEFT JOIN users g ON g.id = reg.guest_user_id AND g.deleted_at IS NULL
         """;

    /// <summary>Needs alias <c>res</c> (reservations).</summary>
    internal const string ReservationStatusSql =
        """
        CASE WHEN res.cancelled_at IS NOT NULL THEN 'cancelled'
             WHEN res.returned_at IS NOT NULL THEN 'returned'
             WHEN now() >= res.deliver_at THEN 'delivered'
             ELSE 'held' END
        """;

    private static Reservation ToReservation(ReservationRow r, Guid? viewer)
    {
        var holder = viewer == r.HolderId;
        return new Reservation(r.Id, r.AllocationId, r.EventId, r.RegistrationId, r.Status, holder ? "holder" : "guest", r.Seats,
            r.FaceValueCents, r.Currency, r.DeliverAt, r.CreatedAt, r.ReturnedAt, r.CancelledAt, r.CancelledReason, r.HolderHandle,
            r.GuestHandle, holder && r.Status is "held" or "delivered" && r.BeforeShow,
            holder && r.Status == "delivered" ? new Ticket(r.Token, r.HolderHandle, r.GuestHandle) : null,
            new Show(r.EventId, r.ArtistId, r.Name, r.Venue, r.CityId, r.City, r.Region, r.Country, r.StartsAt, r.TicketsAvailable),
            new ScoringEndpoints.ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl), r.AllocationName);
    }

    private static ApiProblemException ReservationUnavailable(string detail) =>
        new(StatusCodes.Status409Conflict, "reservation_unavailable", "Reservation can't be returned", detail);

    // The show's columns are selected under their own names (not EventColumns: its Id and ArtistId would collide).
    private sealed class ReservationRow
    {
        public Guid Id { get; set; }
        public Guid AllocationId { get; set; }
        public Guid EventId { get; set; }
        public Guid RegistrationId { get; set; }
        public Guid HolderId { get; set; }
        public string Status { get; set; } = "";
        public int Seats { get; set; }
        public int FaceValueCents { get; set; }
        public string Currency { get; set; } = "";
        public DateTime DeliverAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string? CancelledReason { get; set; }
        public string HolderHandle { get; set; } = "";
        public string? GuestHandle { get; set; }
        public string Token { get; set; } = "";
        public bool BeforeShow { get; set; }
        public string AllocationName { get; set; } = "";
        public Guid ArtistId { get; set; }
        public string ArtistName { get; set; } = "";
        public string? ArtistImageUrl { get; set; }
        public string Name { get; set; } = "";
        public string? Venue { get; set; }
        public string? CityId { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public DateTime StartsAt { get; set; }
        public bool TicketsAvailable { get; set; }
    }
}
