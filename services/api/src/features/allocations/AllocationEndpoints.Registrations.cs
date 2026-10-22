using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// Fan registration (migration 0011): check eligibility, register, withdraw, and list. The database functions
/// <c>registration_blocker()</c>, <c>register_for_allocation()</c>, and <c>withdraw_registration()</c> hold the rules;
/// a registration keeps a snapshot of the fan's hub cities and score from when they registered.
/// </summary>
public static partial class AllocationEndpoints
{
    private static void MapRegistrations(RouteGroupBuilder v1)
    {
        v1.MapGet("/allocations/{allocationId:guid}/eligibility", GetEligibilityAsync);
        v1.MapPut("/allocations/{allocationId:guid}/registration", RegisterAsync);
        v1.MapDelete("/allocations/{allocationId:guid}/registration", WithdrawAsync);
        v1.MapGet("/me/registrations", ListMyRegistrationsAsync);
    }

    /// <summary><c>HubMatch</c>: where the show's city is in the fan's hub cities (1 primary, 2-3 backup), or null.</summary>
    public sealed record Registration(
        Guid Id, Guid AllocationId, Guid EventId, IReadOnlyList<string> HubCityIds, short? HubMatch, bool Local,
        decimal Percentile, decimal RawScore, bool IsEstimated, DateTime RegisteredAt, DateTime? WithdrawnAt, string Status);

    /// <summary>
    /// Whether the fan can register now. <c>Reason</c> is null when they can (or already have), else one of
    /// not_open, artist_not_active, hub_city_required, no_score, below_threshold, already_registered.
    /// </summary>
    public sealed record Eligibility(
        Guid AllocationId, bool Eligible, string? Reason, decimal MinPercentile, decimal? Percentile, bool? IsEstimated,
        short? HubMatch, Registration? Registration);

    public sealed record MyRegistration(Registration Registration, Allocation Allocation, Show Event);

    public sealed record MyRegistrations(IReadOnlyList<MyRegistration> Items);

    private static async Task<IResult> GetEligibilityAsync(
        ClaimsPrincipal user, Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
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
            SELECT s.percentile AS Percentile, s.is_estimated AS IsEstimated,
                   (SELECT h.position FROM user_hub_cities h WHERE h.user_id = @userId AND h.city_id = ev.city_id) AS HubMatch
            FROM allocations a JOIN events ev ON ev.id = a.event_id
            LEFT JOIN user_artist_scores s ON s.artist_id = ev.artist_id AND s.user_id = @userId
            WHERE a.id = @allocationId
            """,
            new { userId, allocationId });
        return Results.Ok(new Eligibility(allocationId, reason is null, reason, allocation.MinPercentile, current?.Percentile,
            current?.IsEstimated, current?.HubMatch, registration));
    }

    private static async Task<IResult> RegisterAsync(
        ClaimsPrincipal user, Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await LoadPublishedAllocationAsync(connection, allocationId);
        var existing = await LoadRegistrationAsync(connection, userId, allocationId);
        if (existing is not null)
        {
            return Results.Ok(existing);
        }

        try
        {
            await connection.ExecuteAsync("SELECT register_for_allocation(@userId, @allocationId)", new { userId, allocationId });
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

        var registration = await LoadRegistrationAsync(connection, userId, allocationId)
                           ?? throw Blocked("not_open");   // withdrawn in between (cancelled), so not live
        return Results.Created($"/v1/allocations/{allocationId}/registration", registration);
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
            $"SELECT {RegistrationColumns} FROM allocation_registrations r WHERE r.user_id = @userId ORDER BY r.registered_at DESC LIMIT 100",
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
            "You're registered for another allocation of this show. Withdraw from it first."),
        "no_score" => new(StatusCodes.Status403Forbidden, "not_eligible", "Not eligible",
            "You don't have a fandom score for this artist yet. Connect Spotify and listen to them."),
        "below_threshold" => new(StatusCodes.Status403Forbidden, "not_eligible", "Not eligible",
            "Your fandom score for this artist is below this allocation's threshold."),
        _ => new(StatusCodes.Status409Conflict, "conflict", "Conflict", reason),
    };

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
             SELECT {RegistrationColumns} FROM allocation_registrations r
             WHERE r.user_id = @userId AND r.allocation_id = @allocationId AND r.withdrawn_at IS NULL
             """,
            new { userId, allocationId });
        return row is null ? null : ToRegistration(row);
    }

    private const string RegistrationColumns =
        """
        r.id AS Id, r.allocation_id AS AllocationId, r.event_id AS EventId, r.hub_city_ids AS HubCityIds,
        r.hub_match AS HubMatch, r.percentile AS Percentile, r.raw_score AS RawScore, r.is_estimated AS IsEstimated,
        r.registered_at AS RegisteredAt, r.withdrawn_at AS WithdrawnAt
        """;

    private static Registration ToRegistration(RegistrationRow row) =>
        new(row.Id, row.AllocationId, row.EventId, row.HubCityIds, row.HubMatch, row.HubMatch is not null, row.Percentile,
            row.RawScore, row.IsEstimated, row.RegisteredAt, row.WithdrawnAt, row.WithdrawnAt is null ? "registered" : "withdrawn");

    // A class with setters, not a positional record: Dapper reports text[] as System.Array to constructors.
    private sealed class RegistrationRow
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
    }

    private sealed record CurrentStanding(decimal? Percentile, bool? IsEstimated, short? HubMatch);
}
