using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// Shows and their fan allocations (migration 0013). Fans read published allocations (<c>/artists/{id}/events</c>,
/// <c>/events/{id}</c>); staff create and edit shows and allocations under <c>/admin</c> until promoter
/// organizations exist. An allocation starts as a draft, is published to fans, and its rules lock once registration
/// opens (database triggers enforce that, the show's time and city, and the publishing preconditions).
/// </summary>
public static partial class AllocationEndpoints
{
    // A date and time with a UTC offset (RFC 3339): times without one would be read in the server's time zone.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial System.Text.RegularExpressions.Regex OffsetTime();

    public static void MapAllocations(this RouteGroupBuilder v1)
    {
        v1.MapGet("/artists/{artistId:guid}/events", ListArtistEventsAsync);
        v1.MapGet("/events/{eventId:guid}", GetEventAsync);
        MapRegistrations(v1);

        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        MapReservations(v1, admin);
        admin.MapPost("/events", CreateEventAsync);
        admin.MapPatch("/events/{eventId:guid}", UpdateEventAsync);
        admin.MapGet("/events/{eventId:guid}", GetEventForStaffAsync);
        admin.MapPost("/events/{eventId:guid}/allocations", CreateAllocationAsync);
        admin.MapPatch("/allocations/{allocationId:guid}", UpdateAllocationAsync);
        admin.MapPost("/allocations/{allocationId:guid}/publish", PublishAsync);
        admin.MapPost("/allocations/{allocationId:guid}/cancel", CancelAsync);
        admin.MapDelete("/allocations/{allocationId:guid}", DeleteDraftAsync);
        MapSelection(admin);
    }

    /// <summary>
    /// A show. Staff-entered shows have <c>Source</c> null and <c>Status</c> scheduled; shows loaded from Ticketmaster
    /// (migration 0030, workers/concerts) carry its link, status, public sale, and presales.
    /// </summary>
    public sealed record Show(
        Guid Id, Guid ArtistId, string Name, string? Venue, string? CityId, string? City, string? Region, string? Country,
        DateTime StartsAt, bool TicketsAvailable, string? Source = null, string? TicketUrl = null, string Status = "scheduled",
        bool TimeTba = false, DateTime? PublicSaleStartsAt = null, DateTime? PublicSaleEndsAt = null)
    {
        /// <summary>Presales that haven't ended, earliest first.</summary>
        public IReadOnlyList<Presale> Presales { get; init; } = [];
    }

    public sealed record Presale(string Name, DateTime StartsAt, DateTime? EndsAt);

    public sealed record EventPage(IReadOnlyList<Show> Items, string? NextCursor);

    public sealed record EventDetail(Show Event, IReadOnlyList<Allocation> Allocations);

    /// <summary>
    /// <c>Status</c>: <c>draft</c> (staff only), <c>upcoming</c> (published, registration not open yet), <c>open</c>,
    /// <c>closed</c> (registration over, no draw yet), <c>selected</c> (the draw has run), or <c>cancelled</c>.
    /// <c>MinRawScore</c> is null on a draft without one: publishing sets the two-saved-albums default.
    /// <c>SeatsLeft</c> is set once the draw has run (seats a selected fan's +1 can still take).
    /// <c>DeliveryHoursBeforeShow</c>: tickets are delivered this long before the show (migration 0020).
    /// </summary>
    public sealed record Allocation(
        Guid Id, Guid EventId, string Name, int Tickets, int FaceValueCents, string Currency, decimal? MinRawScore,
        DateTime RegistrationOpensAt, DateTime RegistrationClosesAt, string Status, DateTime? PublishedAt,
        DateTime? CancelledAt, DateTime? SelectedAt, int? SeatsLeft, int DeliveryHoursBeforeShow);

    // ------------------------------------------------------------------ fans

    private static async Task<IResult> ListArtistEventsAsync(
        Guid artistId, [FromQuery(Name = "include_past")] bool? includePast, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId AND status IN ('onboarding', 'active'))", new { artistId }))
        {
            throw NotFound("No tracked artist has this id.");
        }

        var pageSize = limit ?? 25;
        var rows = (await connection.QueryAsync<Show>(
            $"""
             SELECT {EventColumns} FROM events e LEFT JOIN cities c ON c.id = e.city_id
             WHERE e.artist_id = @artistId AND (@includePast OR e.starts_at > now())
             ORDER BY e.starts_at, e.id
             LIMIT @take OFFSET @offset
             """,
            new { artistId, includePast = includePast ?? false, take = pageSize + 1, offset })).ToList();
        return Results.Ok(new EventPage(await WithPresalesAsync(connection, rows.Take(pageSize)),
            rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> GetEventAsync(Guid eventId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadEventAsync(connection, eventId, includeDrafts: false));
    }

    // ------------------------------------------------------------------ staff: shows

    private static async Task<IResult> GetEventForStaffAsync(Guid eventId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadEventAsync(connection, eventId, includeDrafts: true));
    }

    private static async Task<IResult> CreateEventAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var input = new BodyReader(body);
        var artistId = input.Guid("artist_id", required: true);
        var name = input.Text("name", 200, required: true);
        var venue = input.Text("venue", 200, required: false, nullable: true);
        var cityId = input.Text("city_id", 100, required: true);
        var startsAt = input.Time("starts_at", required: true);
        input.ThrowIfInvalid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId AND status IN ('onboarding', 'active'))", new { artistId }))
        {
            throw ApiProblemException.Validation(new FieldError("artist_id", "Use the id of a tracked artist."));
        }

        await RequireCityAsync(connection, cityId!);
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO events (artist_id, name, venue, city_id, city, country, starts_at, created_by)
            SELECT @artistId, @name, @venue, c.id, c.name, c.country, @startsAt, @createdBy FROM cities c WHERE c.id = @cityId
            RETURNING id
            """,
            new { artistId, name, venue = venue.Value, cityId, startsAt, createdBy = user.UserId() });
        return Results.Created($"/v1/events/{eventId}", await LoadEventAsync(connection, eventId, includeDrafts: true));
    }

    private static async Task<IResult> UpdateEventAsync(
        Guid eventId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var input = new BodyReader(body);
        var name = input.Text("name", 200, required: false);
        var venue = input.Text("venue", 200, required: false, nullable: true);
        var cityId = input.Text("city_id", 100, required: false);
        var startsAt = input.Time("starts_at", required: false);
        input.ThrowIfInvalid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (cityId is not null)
        {
            await RequireCityAsync(connection, cityId);
        }

        var updated = await GuardAsync(() => connection.ExecuteAsync(
            """
            UPDATE events e
            SET name = COALESCE(@name, e.name),
                venue = CASE WHEN @setVenue THEN @venue ELSE e.venue END,
                city_id = COALESCE(@cityId, e.city_id),
                city = COALESCE((SELECT name FROM cities WHERE id = @cityId), e.city),
                country = COALESCE((SELECT country FROM cities WHERE id = @cityId), e.country),
                starts_at = COALESCE(@startsAt, e.starts_at)
            WHERE e.id = @eventId
            """,
            new { eventId, name, setVenue = venue.Present, venue = venue.Value, cityId, startsAt }), field: "starts_at");
        if (updated == 0)
        {
            throw NotFound("No show has this id.");
        }

        return Results.Ok(await LoadEventAsync(connection, eventId, includeDrafts: true));
    }

    // ------------------------------------------------------------------ staff: allocations

    private static async Task<IResult> CreateAllocationAsync(
        ClaimsPrincipal user, Guid eventId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var input = new BodyReader(body);
        var name = input.Text("name", 100, required: false);
        var tickets = input.Integer("tickets", 1, 100_000, required: true);
        var faceValue = input.Integer("face_value_cents", 0, 10_000_000, required: true);
        var currency = input.Currency("currency");
        var minRawScore = input.Score("min_raw_score");
        var opensAt = input.Time("registration_opens_at", required: true);
        var closesAt = input.Time("registration_closes_at", required: true);
        var deliveryHours = input.Integer("delivery_hours_before_show", 0, 24 * 30, required: false);
        input.RequireOrder(opensAt, closesAt);
        input.ThrowIfInvalid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM events WHERE id = @eventId)", new { eventId }))
        {
            throw NotFound("No show has this id.");
        }

        var allocationId = await GuardAsync(() => connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, name, tickets, face_value_cents, currency, min_raw_score,
                                     registration_opens_at, registration_closes_at, delivery_hours_before_show, created_by)
            VALUES (@eventId, COALESCE(@name, 'Fan allocation'), @tickets, @faceValue, COALESCE(@currency, 'USD'),
                    @minRawScore, @opensAt, @closesAt, COALESCE(@deliveryHours, 24), @createdBy)
            RETURNING id
            """,
            new { eventId, name, tickets, faceValue, currency, minRawScore, opensAt, closesAt, deliveryHours, createdBy = user.UserId() }),
            field: "registration_closes_at");
        return Results.Created($"/v1/admin/allocations/{allocationId}", await LoadAllocationAsync(connection, allocationId));
    }

    private static async Task<IResult> UpdateAllocationAsync(
        Guid allocationId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var input = new BodyReader(body);
        var name = input.Text("name", 100, required: false);
        var tickets = input.Integer("tickets", 1, 100_000, required: false);
        var faceValue = input.Integer("face_value_cents", 0, 10_000_000, required: false);
        var currency = input.Currency("currency");
        var minRawScore = input.Score("min_raw_score");
        var opensAt = input.Time("registration_opens_at", required: false);
        var closesAt = input.Time("registration_closes_at", required: false);
        var deliveryHours = input.Integer("delivery_hours_before_show", 0, 24 * 30, required: false);
        input.ThrowIfInvalid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var updated = await GuardAsync(() => connection.ExecuteAsync(
            """
            UPDATE allocations
            SET name = COALESCE(@name, name), tickets = COALESCE(@tickets, tickets),
                face_value_cents = COALESCE(@faceValue, face_value_cents), currency = COALESCE(@currency, currency),
                min_raw_score = COALESCE(@minRawScore, min_raw_score),
                registration_opens_at = COALESCE(@opensAt, registration_opens_at),
                registration_closes_at = COALESCE(@closesAt, registration_closes_at),
                delivery_hours_before_show = COALESCE(@deliveryHours, delivery_hours_before_show)
            WHERE id = @allocationId
            """,
            new { allocationId, name, tickets, faceValue, currency, minRawScore, opensAt, closesAt, deliveryHours }),
            field: "registration_closes_at");
        if (updated == 0)
        {
            throw NotFound("No allocation has this id.");
        }

        return Results.Ok(await LoadAllocationAsync(connection, allocationId));
    }

    private static async Task<IResult> PublishAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var current = await LoadAllocationAsync(connection, allocationId);
        if (current.Status != "draft")
        {
            return Results.Ok(current);   // already published: publishing again changes nothing
        }

        await GuardAsync(() => connection.ExecuteAsync(
            "UPDATE allocations SET published_at = now() WHERE id = @allocationId AND published_at IS NULL", new { allocationId }),
            field: null);
        return Results.Ok(await LoadAllocationAsync(connection, allocationId));
    }

    private static async Task<IResult> CancelAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var current = await LoadAllocationAsync(connection, allocationId);
        if (current.Status == "draft")
        {
            throw Conflict("A draft isn't visible to fans; delete it instead of cancelling.");
        }

        await GuardAsync(() => connection.ExecuteAsync(
            "UPDATE allocations SET cancelled_at = now() WHERE id = @allocationId AND cancelled_at IS NULL", new { allocationId }),
            field: null);
        return Results.Ok(await LoadAllocationAsync(connection, allocationId));
    }

    private static async Task<IResult> DeleteDraftAsync(Guid allocationId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var current = await LoadAllocationAsync(connection, allocationId);
        if (current.Status != "draft")
        {
            throw Conflict("Only drafts can be deleted. Cancel a published allocation instead.");
        }

        await connection.ExecuteAsync("DELETE FROM allocations WHERE id = @allocationId AND published_at IS NULL", new { allocationId });
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ loading

    internal const string EventColumns =
        """
        e.id AS Id, e.artist_id AS ArtistId, e.name AS Name, e.venue AS Venue, e.city_id AS CityId,
        COALESCE(c.name, e.city) AS City, c.region AS Region, COALESCE(c.country, e.country) AS Country,
        e.starts_at AS StartsAt,
        EXISTS (SELECT 1 FROM allocations a WHERE a.event_id = e.id AND a.published_at IS NOT NULL
                  AND a.cancelled_at IS NULL AND a.registration_closes_at > now()) AS TicketsAvailable,
        e.external_source AS Source, e.ticket_url AS TicketUrl, e.status AS Status, e.time_tba AS TimeTba,
        e.public_sale_starts_at AS PublicSaleStartsAt, e.public_sale_ends_at AS PublicSaleEndsAt
        """;

    /// <summary>Adds each show's presales that haven't ended (migration 0030).</summary>
    internal static async Task<List<Show>> WithPresalesAsync(NpgsqlConnection connection, IEnumerable<Show> shows)
    {
        var list = shows.ToList();
        if (list.Count == 0)
        {
            return list;
        }

        var presales = (await connection.QueryAsync<(Guid EventId, string Name, DateTime StartsAt, DateTime? EndsAt)>(
                """
                SELECT event_id, name, starts_at, ends_at FROM event_presales
                WHERE event_id = ANY(@ids) AND (ends_at IS NULL OR ends_at > now())
                ORDER BY starts_at, name
                """,
                new { ids = list.Select(s => s.Id).ToArray() }))
            .ToLookup(p => p.EventId, p => new Presale(p.Name, p.StartsAt, p.EndsAt));
        return [.. list.Select(s => presales.Contains(s.Id) ? s with { Presales = [.. presales[s.Id]] } : s)];
    }

    internal const string AllocationColumns =
        """
        a.id AS Id, a.event_id AS EventId, a.name AS Name, a.tickets AS Tickets, a.face_value_cents AS FaceValueCents,
        a.currency AS Currency, a.min_raw_score AS MinRawScore, a.registration_opens_at AS RegistrationOpensAt,
        a.registration_closes_at AS RegistrationClosesAt,
        CASE WHEN a.cancelled_at IS NOT NULL THEN 'cancelled'
             WHEN a.published_at IS NULL THEN 'draft'
             WHEN a.selected_at IS NOT NULL THEN 'selected'
             WHEN now() < a.registration_opens_at THEN 'upcoming'
             WHEN now() < a.registration_closes_at THEN 'open'
             ELSE 'closed' END AS Status,
        a.published_at AS PublishedAt, a.cancelled_at AS CancelledAt, a.selected_at AS SelectedAt,
        (SELECT sel.tickets - sel.seats_assigned FROM allocation_selections sel WHERE sel.allocation_id = a.id) AS SeatsLeft,
        a.delivery_hours_before_show AS DeliveryHoursBeforeShow
        """;

    internal static async Task<EventDetail> LoadEventAsync(NpgsqlConnection connection, Guid eventId, bool includeDrafts)
    {
        var show = await connection.QuerySingleOrDefaultAsync<Show>(
            $"""
             SELECT {EventColumns} FROM events e LEFT JOIN cities c ON c.id = e.city_id
             JOIN artists ar ON ar.id = e.artist_id
             WHERE e.id = @eventId AND (@includeDrafts OR ar.status IN ('onboarding', 'active'))
             """,
            new { eventId, includeDrafts }) ?? throw NotFound("No show has this id.");
        var allocations = await connection.QueryAsync<Allocation>(
            $"""
             SELECT {AllocationColumns} FROM allocations a
             WHERE a.event_id = @eventId AND (@includeDrafts OR a.published_at IS NOT NULL)
             ORDER BY a.registration_opens_at, a.created_at
             """,
            new { eventId, includeDrafts });
        return new EventDetail((await WithPresalesAsync(connection, [show]))[0], [.. allocations]);
    }

    private static async Task<Allocation> LoadAllocationAsync(NpgsqlConnection connection, Guid allocationId) =>
        await connection.QuerySingleOrDefaultAsync<Allocation>(
            $"SELECT {AllocationColumns} FROM allocations a WHERE a.id = @allocationId", new { allocationId })
        ?? throw NotFound("No allocation has this id.");

    private static async Task RequireCityAsync(NpgsqlConnection connection, string cityId)
    {
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM cities WHERE id = @cityId AND active)", new { cityId }))
        {
            throw ApiProblemException.Validation(new FieldError("city_id", "Use a city id from GET /cities."));
        }
    }

    // The database triggers have the last word on timing, locking, and publishing (migration 0013).
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, string? field)
    {
        try
        {
            return await action();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException)
        {
            var message = e.MessageText;
            if (message.Contains("before the show starts", StringComparison.Ordinal))
            {
                throw ApiProblemException.Validation(new FieldError(field ?? "registration_closes_at",
                    "Registration must close before the show starts."));
            }

            if (message.Contains("locked", StringComparison.Ordinal))
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "allocation_locked", "Allocation is locked",
                    "Registration has opened, so the allocation's rules and the show's time and city can't change. Cancel it instead.");
            }

            if (message.StartsWith("allocation:", StringComparison.Ordinal) || message.StartsWith("event:", StringComparison.Ordinal))
            {
                throw Conflict(char.ToUpperInvariant(message[message.IndexOf(':') + 2]) + message[(message.IndexOf(':') + 3)..] + ".");
            }

            throw;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName == "allocations_window_check")
        {
            throw ApiProblemException.Validation(new FieldError("registration_closes_at", "Registration must close after it opens."));
        }
    }

    private static ApiProblemException NotFound(string detail) =>
        new(StatusCodes.Status404NotFound, "not_found", "Not found", detail);

    private static ApiProblemException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, "conflict", "Conflict", detail);

    /// <summary>Reads a JSON object body field by field, collecting validation errors (missing fields stay null).</summary>
    private sealed class BodyReader
    {
        private readonly JsonElement _body;
        private readonly List<FieldError> _errors = [];

        public BodyReader(JsonElement body)
        {
            _body = body;
            if (body.ValueKind != JsonValueKind.Object)
            {
                throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
            }
        }

        public void ThrowIfInvalid()
        {
            if (_errors.Count > 0)
            {
                throw ApiProblemException.Validation([.. _errors]);
            }
        }

        public (bool Present, string? Value) Text(string field, int maxLength, bool required, bool nullable) =>
            !_body.TryGetProperty(field, out var value)
                ? Missing<(bool, string?)>(field, required, (false, null))
                : value.ValueKind == JsonValueKind.Null && nullable
                    ? (true, null)
                    : value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text && text.Length <= maxLength
                        ? (true, text)
                        : Invalid<(bool, string?)>(field, $"Send 1 to {maxLength} characters{(nullable ? ", or null" : "")}.", (false, null));

        public string? Text(string field, int maxLength, bool required) => Text(field, maxLength, required, nullable: false).Value;

        public Guid? Guid(string field, bool required) =>
            !_body.TryGetProperty(field, out var value)
                ? Missing<Guid?>(field, required, null)
                : value.ValueKind == JsonValueKind.String && System.Guid.TryParse(value.GetString(), out var id)
                    ? id
                    : Invalid<Guid?>(field, "Send an id.", null);

        public DateTime? Time(string field, bool required) =>
            !_body.TryGetProperty(field, out var value)
                ? Missing<DateTime?>(field, required, null)
                : value.ValueKind == JsonValueKind.String && OffsetTime().IsMatch(value.GetString()!)
                  && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? time.UtcDateTime
                    : Invalid<DateTime?>(field, "Send a date and time with its offset, e.g. 2026-11-20T20:00:00-06:00.", null);

        public int? Integer(string field, int min, int max, bool required) =>
            !_body.TryGetProperty(field, out var value)
                ? Missing<int?>(field, required, null)
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
                    ? number
                    : Invalid<int?>(field, $"Send a whole number from {min} to {max}.", null);

        public decimal? Score(string field) =>
            !_body.TryGetProperty(field, out var value)
                ? null
                : value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number is >= 0 and < 100_000_000
                    ? Math.Round(number, 2)
                    : Invalid<decimal?>(field, "Send a raw score of 0 or more.", null);

        public string? Currency(string field) =>
            !_body.TryGetProperty(field, out var value)
                ? null
                : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 3 } code && code.All(char.IsAsciiLetterUpper)
                    ? code
                    : Invalid<string?>(field, "Send a 3-letter currency code such as USD or CAD.", null);

        public void RequireOrder(DateTime? opensAt, DateTime? closesAt)
        {
            if (opensAt is not null && closesAt is not null && closesAt <= opensAt)
            {
                _errors.Add(new FieldError("registration_closes_at", "Registration must close after it opens."));
            }
        }

        private T Missing<T>(string field, bool required, T fallback)
        {
            if (required)
            {
                _errors.Add(new FieldError(field, "Required."));
            }

            return fallback;
        }

        private T Invalid<T>(string field, string message, T fallback)
        {
            _errors.Add(new FieldError(field, message));
            return fallback;
        }
    }
}
