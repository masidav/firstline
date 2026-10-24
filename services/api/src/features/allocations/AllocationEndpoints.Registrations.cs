using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// Fan registration (migrations 0013 and 0017): check eligibility, register (with or without a +1), withdraw, name
/// the +1's account, and list. The database functions <c>registration_blocker()</c>, <c>register_for_allocation()</c>,
/// <c>withdraw_registration()</c>, <c>add_plus_one()</c>, and <c>name_plus_one_guest()</c> hold the rules; a
/// registration keeps a snapshot of the fan's hub cities and score from when they registered.
/// </summary>
public static partial class AllocationEndpoints
{
    private static void MapRegistrations(RouteGroupBuilder v1)
    {
        v1.MapGet("/allocations/{allocationId:guid}/eligibility", GetEligibilityAsync);
        v1.MapPut("/allocations/{allocationId:guid}/registration", RegisterAsync);
        v1.MapDelete("/allocations/{allocationId:guid}/registration", WithdrawAsync);
        v1.MapPut("/allocations/{allocationId:guid}/registration/guest", NameGuestAsync);
        v1.MapGet("/me/registrations", ListMyRegistrationsAsync);
    }

    /// <summary>Optional body of <c>PUT /registration</c>. Null leaves the +1 as it is (none for a new registration).</summary>
    public sealed record RegistrationRequest(bool? PlusOne);

    public sealed record GuestRequest(string? Handle);

    /// <summary>The +1's account.</summary>
    public sealed record Guest(Guid Id, string Handle);

    /// <summary>
    /// <c>HubMatch</c>: where the show's city is in the fan's hub cities (1 primary, 2-3 backup), or null.
    /// <c>Status</c>: registered, withdrawn, selected, or not_selected. <c>Seats</c>: 1, or 2 with the +1, once selected.
    /// </summary>
    public sealed record Registration(
        Guid Id, Guid AllocationId, Guid EventId, IReadOnlyList<string> HubCityIds, short? HubMatch, bool Local,
        decimal Percentile, decimal RawScore, bool IsEstimated, DateTime RegisteredAt, DateTime? WithdrawnAt, string Status,
        bool PlusOne, Guest? Guest, int? SelectionRank, int? Seats);

    /// <summary>
    /// Whether the fan can register now. <c>Reason</c> is null when they can (or already have), else one of
    /// not_open, artist_not_active, hub_city_required, no_score, below_threshold, already_registered.
    /// </summary>
    public sealed record Eligibility(
        Guid AllocationId, bool Eligible, string? Reason, decimal? MinRawScore, decimal? RawScore, decimal? Percentile,
        bool? IsEstimated, short? HubMatch, Registration? Registration, Standing? Standing);

    /// <summary>
    /// Where the fan would place in the draw if registration closed now, counting ties against them. <c>Likely</c>
    /// is true only when everyone ranked ahead plus the fan (and their +1) fit within the tickets minus
    /// <c>MarginSeats</c>, a buffer for fans who register later with higher scores.
    /// </summary>
    public sealed record Standing(
        int Rank, int Registrants, int TopPercent, int SeatsAhead, int SeatsNeeded, int Tickets, int MarginSeats,
        bool Local, bool Likely);

    public sealed record MyRegistration(Registration Registration, Allocation Allocation, Show Event);

    public sealed record MyRegistrations(IReadOnlyList<MyRegistration> Items);

    // plus_one: for a fan who hasn't registered yet, whether to count a +1 in the standing.
    private static async Task<IResult> GetEligibilityAsync(
        ClaimsPrincipal user, Guid allocationId, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "plus_one")] bool? plusOne,
        NpgsqlDataSource db, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var allocation = await LoadPublishedAllocationAsync(connection, allocationId);
        var registration = await LoadRegistrationAsync(connection, userId, allocationId);
        var reason = registration is null
            ? await connection.ExecuteScalarAsync<string?>("SELECT registration_blocker(@userId, @allocationId)", new { userId, allocationId })
            : null;
        var current = await connection.QuerySingleOrDefaultAsync<CurrentStanding>(
            """
            SELECT s.raw_score AS RawScore, s.percentile AS Percentile, s.is_estimated AS IsEstimated,
                   (SELECT h.position FROM user_hub_cities h WHERE h.user_id = @userId AND h.city_id = ev.city_id) AS HubMatch
            FROM allocations a JOIN events ev ON ev.id = a.event_id
            LEFT JOIN user_artist_scores s ON s.artist_id = ev.artist_id AND s.user_id = @userId
            WHERE a.id = @allocationId
            """,
            new { userId, allocationId });
        var standing = allocation.Status is "upcoming" or "open" && (reason is null || registration is not null)
            ? await LoadStandingAsync(connection, userId, allocation, plusOne ?? false,
                configuration.GetValue("Allocations:LikelyMarginSeats", DefaultLikelyMarginSeats))
            : null;
        return Results.Ok(new Eligibility(allocationId, reason is null, reason, allocation.MinRawScore, current?.RawScore,
            current?.Percentile, current?.IsEstimated, current?.HubMatch, registration, standing));
    }

    /// <summary>Seats kept back before telling a fan they'll most likely get a ticket (decided 2026-10-24: 25 to start).</summary>
    private const int DefaultLikelyMarginSeats = 25;

    // Ranks the fan like the draw does (hub fans first, then raw score), but counts equal scores as ahead of them,
    // since the draw's tiebreak is random. A registered fan is ranked by their snapshot; otherwise by today's score.
    private static async Task<Standing?> LoadStandingAsync(
        NpgsqlConnection connection, Guid userId, Allocation allocation, bool plusOneIfNew, int marginSeats)
    {
        var row = await connection.QuerySingleOrDefaultAsync<StandingRow>(
            """
            WITH me AS (
              SELECT CASE WHEN r.id IS NOT NULL THEN r.hub_match IS NOT NULL ELSE h.position IS NOT NULL END AS local,
                     COALESCE(r.raw_score, s.raw_score) AS raw_score,
                     COALESCE(r.plus_one, @plusOneIfNew) AS plus_one
                FROM allocations a JOIN events ev ON ev.id = a.event_id
                LEFT JOIN allocation_registrations r ON r.allocation_id = a.id AND r.user_id = @userId AND r.withdrawn_at IS NULL
                LEFT JOIN user_artist_scores s ON s.artist_id = ev.artist_id AND s.user_id = @userId
                LEFT JOIN user_hub_cities h ON h.user_id = @userId AND h.city_id = ev.city_id
               WHERE a.id = @allocationId
            ), others AS (
              SELECT o.raw_score, o.plus_one, o.hub_match IS NOT NULL AS local
                FROM allocation_registrations o
               WHERE o.allocation_id = @allocationId AND o.withdrawn_at IS NULL AND o.user_id <> @userId
            )
            SELECT me.local AS Local, me.plus_one AS PlusOne,
                   (SELECT count(*)::int FROM others) AS Others,
                   (SELECT count(*)::int FROM others o
                     WHERE o.local > me.local OR (o.local = me.local AND o.raw_score >= me.raw_score)) AS Ahead,
                   (SELECT COALESCE(sum(CASE WHEN o.plus_one THEN 2 ELSE 1 END), 0)::int FROM others o
                     WHERE o.local > me.local OR (o.local = me.local AND o.raw_score >= me.raw_score)) AS SeatsAhead
              FROM me WHERE me.raw_score IS NOT NULL
            """,
            new { userId, allocationId = allocation.Id, plusOneIfNew });
        if (row is null)
        {
            return null;
        }

        var registrants = row.Others + 1;
        var rank = row.Ahead + 1;
        var seatsNeeded = row.PlusOne ? 2 : 1;
        return new Standing(rank, registrants, (int)Math.Ceiling(100.0 * rank / registrants), row.SeatsAhead, seatsNeeded,
            allocation.Tickets, marginSeats, row.Local, row.SeatsAhead + seatsNeeded <= allocation.Tickets - marginSeats);
    }

    private sealed record StandingRow(bool Local, bool PlusOne, int Others, int Ahead, int SeatsAhead);

    // Registers (201), or updates the +1 on an existing registration (200): while registration is open the +1 can be
    // turned on or off; after being selected it can be added if a seat is free.
    private static async Task<IResult> RegisterAsync(
        ClaimsPrincipal user, Guid allocationId, RegistrationRequest? request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        var plusOne = request?.PlusOne;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var allocation = await LoadPublishedAllocationAsync(connection, allocationId);
        var existing = await LoadRegistrationAsync(connection, userId, allocationId);
        if (existing is not null)
        {
            // A selected fan whose +1 didn't fit in the draw (one seat) can ask again in case a seat is free.
            var retryingPlusOne = plusOne == true && existing.Status == "selected" && existing.Seats == 1;
            if (plusOne is null || (plusOne == existing.PlusOne && !retryingPlusOne))
            {
                return Results.Ok(existing);
            }

            await ChangePlusOneAsync(connection, userId, allocation, existing, plusOne.Value);
            return Results.Ok(await LoadRegistrationAsync(connection, userId, allocationId));
        }

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await connection.ExecuteAsync("SELECT register_for_allocation(@userId, @allocationId)", new { userId, allocationId }, transaction);
                if (plusOne == true)
                {
                    await connection.ExecuteAsync(
                        "UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = @allocationId AND user_id = @userId AND withdrawn_at IS NULL",
                        new { userId, allocationId }, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText.StartsWith("registration blocked: ", StringComparison.Ordinal))
            {
                throw Blocked(e.MessageText["registration blocked: ".Length..]);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // A concurrent request for another allocation of the same show won.
                throw Blocked("already_registered");
            }
        }

        var registration = await LoadRegistrationAsync(connection, userId, allocationId)
                           ?? throw Blocked("not_open");   // withdrawn in between (cancelled), so not live
        return Results.Created($"/v1/allocations/{allocationId}/registration", registration);
    }

    private static async Task ChangePlusOneAsync(
        NpgsqlConnection connection, Guid userId, Allocation allocation, Registration existing, bool plusOne)
    {
        if (existing.Status == "selected")
        {
            if (!plusOne)
            {
                throw PlusOneUnavailable("The draw has run, so your +1 can't be dropped here.");
            }

            try
            {
                await connection.ExecuteAsync("SELECT add_plus_one(@userId, @allocationId)", new { userId, allocationId = allocation.Id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText.StartsWith("plus one blocked: ", StringComparison.Ordinal))
            {
                throw e.MessageText["plus one blocked: ".Length..] switch
                {
                    "sold_out" => PlusOneUnavailable("Every seat in this allocation is taken, so there's no room for a +1."),
                    "show_started" => PlusOneUnavailable("The show has started."),
                    _ => PlusOneUnavailable("Only selected fans can add a +1."),
                };
            }

            return;
        }

        if (allocation.Status != "open")
        {
            throw existing.Status == "not_selected"
                ? PlusOneUnavailable("You weren't selected for this allocation.")
                : Blocked("not_open");
        }

        if (!plusOne && existing.Guest is not null)
        {
            throw PlusOneUnavailable("You've named a guest for your +1, so it can't be dropped.");
        }

        try
        {
            await connection.ExecuteAsync(
                "UPDATE allocation_registrations SET plus_one = @plusOne WHERE id = @id AND withdrawn_at IS NULL",
                new { plusOne, id = existing.Id });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw Blocked("not_open");   // registration closed in between
        }
    }

    private static async Task<IResult> NameGuestAsync(
        ClaimsPrincipal user, Guid allocationId, GuestRequest request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (request.Handle?.Trim().TrimStart('@') is not { Length: > 0 } handle)
        {
            throw ApiProblemException.Validation(new FieldError("handle", "Send your guest's handle."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await LoadPublishedAllocationAsync(connection, allocationId);
        var guestId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE lower(handle) = lower(@handle) AND deleted_at IS NULL", new { handle });
        if (guestId is null)
        {
            throw GuestUnavailable("No account has this handle. Your guest needs to sign up first.");
        }

        try
        {
            await connection.ExecuteAsync("SELECT name_plus_one_guest(@userId, @allocationId, @guestId)", new { userId, allocationId, guestId });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText.StartsWith("guest blocked: ", StringComparison.Ordinal))
        {
            throw e.MessageText["guest blocked: ".Length..] switch
            {
                "not_registered" => NotFound("You're not registered for this allocation."),
                "no_plus_one" => GuestUnavailable("You don't have a +1 seat for this allocation."),
                "already_named" => GuestUnavailable("You've already named your guest. Guests can't be changed."),
                "self" => GuestUnavailable("Your guest has to be someone else."),
                "guest_has_ticket" => GuestUnavailable("This person is already registered for this show or is someone else's guest."),
                _ => GuestUnavailable("This account can't be your guest."),
            };
        }

        return Results.Ok(await LoadRegistrationAsync(connection, userId, allocationId));
    }

    private static async Task<IResult> WithdrawAsync(
        ClaimsPrincipal user, Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await LoadPublishedAllocationAsync(connection, allocationId);
        try
        {
            return await connection.ExecuteScalarAsync<bool>("SELECT withdraw_registration(@userId, @allocationId)", new { userId, allocationId })
                ? Results.NoContent()
                : throw NotFound("You're not registered for this allocation.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText.StartsWith("registration blocked: ", StringComparison.Ordinal))
        {
            throw Blocked(e.MessageText["registration blocked: ".Length..]);
        }
    }

    // Newest first, withdrawn ones included (they're part of the fan's history), at most 100.
    private static async Task<IResult> ListMyRegistrationsAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var registrations = (await connection.QueryAsync<RegistrationRow>(
            $"SELECT {RegistrationColumns} FROM {RegistrationSource} WHERE r.user_id = @userId ORDER BY r.registered_at DESC LIMIT 100",
            new { userId })).Select(ToRegistration).ToList();

        var items = new List<MyRegistration>();
        foreach (var registration in registrations)
        {
            // Includes shows of artists paused since: the fan's history shouldn't vanish.
            var detail = await LoadEventAsync(connection, registration.EventId, includeDrafts: true);
            var allocation = detail.Allocations.Single(a => a.Id == registration.AllocationId);
            items.Add(new MyRegistration(registration, allocation, detail.Event));
        }

        return Results.Ok(new MyRegistrations(items));
    }

    private static ApiProblemException Blocked(string reason) => reason switch
    {
        "not_open" => new(StatusCodes.Status409Conflict, "registration_closed", "Registration isn't open",
            "Registration for this allocation isn't open right now."),
        "artist_not_active" => new(StatusCodes.Status409Conflict, "artist_not_active", "Artist is not active",
            "This artist isn't active on Fandom Score right now, so registration is paused."),
        "hub_city_required" => new(StatusCodes.Status409Conflict, "hub_city_required", "Hub city required",
            "Choose a hub city before registering."),
        "already_registered" => new(StatusCodes.Status409Conflict, "already_registered", "Already registered for this show",
            "You're registered for another allocation of this show, or you're someone's guest for it."),
        "no_score" => new(StatusCodes.Status403Forbidden, "not_eligible", "Not eligible",
            "You don't have a fandom score for this artist yet. Connect Spotify and listen to them."),
        "below_threshold" => new(StatusCodes.Status403Forbidden, "not_eligible", "Not eligible",
            "Your fandom score for this artist is below this allocation's threshold."),
        _ => new(StatusCodes.Status409Conflict, "conflict", "Conflict", reason),
    };

    private static ApiProblemException PlusOneUnavailable(string detail) =>
        new(StatusCodes.Status409Conflict, "plus_one_unavailable", "Can't change the +1", detail);

    private static ApiProblemException GuestUnavailable(string detail) =>
        new(StatusCodes.Status409Conflict, "guest_unavailable", "Can't name this guest", detail);

    // Fans only see published allocations; drafts don't exist for them.
    private static async Task<Allocation> LoadPublishedAllocationAsync(NpgsqlConnection connection, Guid allocationId)
    {
        var allocation = await LoadAllocationAsync(connection, allocationId);
        return allocation.Status == "draft" ? throw NotFound("No allocation has this id.") : allocation;
    }

    private static async Task<Registration?> LoadRegistrationAsync(NpgsqlConnection connection, Guid userId, Guid allocationId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<RegistrationRow>(
            $"""
             SELECT {RegistrationColumns} FROM {RegistrationSource}
             WHERE r.user_id = @userId AND r.allocation_id = @allocationId AND r.withdrawn_at IS NULL
             """,
            new { userId, allocationId });
        return row is null ? null : ToRegistration(row);
    }

    private const string RegistrationSource =
        "allocation_registrations r LEFT JOIN users g ON g.id = r.guest_user_id";

    private const string RegistrationColumns =
        """
        r.id AS Id, r.allocation_id AS AllocationId, r.event_id AS EventId, r.hub_city_ids AS HubCityIds,
        r.hub_match AS HubMatch, r.percentile AS Percentile, r.raw_score AS RawScore, r.is_estimated AS IsEstimated,
        r.registered_at AS RegisteredAt, r.withdrawn_at AS WithdrawnAt, r.plus_one AS PlusOne, r.outcome AS Outcome,
        r.selection_rank AS SelectionRank, r.seats AS Seats, g.id AS GuestId, g.handle AS GuestHandle
        """;

    private static Registration ToRegistration(RegistrationRow row) =>
        new(row.Id, row.AllocationId, row.EventId, row.HubCityIds, row.HubMatch, row.HubMatch is not null, row.Percentile,
            row.RawScore, row.IsEstimated, row.RegisteredAt, row.WithdrawnAt,
            row.WithdrawnAt is not null ? "withdrawn" : row.Outcome ?? "registered",
            row.PlusOne, row.GuestId is { } guestId ? new Guest(guestId, row.GuestHandle!) : null, row.SelectionRank, row.Seats);

    // A class with setters, not a positional record: Dapper reports text[] as System.Array to constructors.
    private class RegistrationRow
    {
        public Guid Id { get; set; }
        public Guid AllocationId { get; set; }
        public Guid EventId { get; set; }
        public string[] HubCityIds { get; set; } = [];
        public short? HubMatch { get; set; }
        public decimal Percentile { get; set; }
        public decimal RawScore { get; set; }
        public bool IsEstimated { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime? WithdrawnAt { get; set; }
        public bool PlusOne { get; set; }
        public string? Outcome { get; set; }
        public int? SelectionRank { get; set; }
        public short? Seats { get; set; }
        public Guid? GuestId { get; set; }
        public string? GuestHandle { get; set; }
    }

    private sealed record CurrentStanding(decimal? RawScore, decimal? Percentile, bool? IsEstimated, short? HubMatch);
}
