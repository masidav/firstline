using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Users;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Fraud;

/// <summary>
/// openapi <c>POST /me/location-checks</c> (migration 0057, <c>docs/security/hub-location-checks.md</c>): the app sends
/// the device position at three moments the fan chooses to share it (setting hub cities, opening a show's standing,
/// registering). The coordinates go to one database function as bound parameters and are never stored; what stays is
/// the nearest curated city, distances rounded to 10 km, and whether a hub city was within reach.
/// </summary>
public static class LocationChecks
{
    public const string HubSet = "hub_set";
    public const string Eligibility = "eligibility";
    public const string Registration = "registration";

    public sealed record LocationCheckRequest(string? Kind, Guid? AllocationId, double? Latitude, double? Longitude, double? AccuracyM);

    /// <summary>openapi LocationCheck. Everything but <see cref="Recorded"/> is null when nothing was recorded.</summary>
    public sealed record LocationCheck(
        bool Recorded, bool? HubMatch, HubCity? NearestCity, int? NearestCityKm, int? NearestHubKm);

    public static void MapLocationChecks(this RouteGroupBuilder v1) =>
        v1.MapPost("/me/location-checks", RecordAsync);

    private sealed record Recorded(bool WasRecorded, string? NearestCityId, int? NearestCityKm, int? NearestHubKm, bool? HubMatch);

    private static async Task<IResult> RecordAsync(
        ClaimsPrincipal user, LocationCheckRequest request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (request.Kind is not (HubSet or Eligibility or Registration))
        {
            errors.Add(new FieldError("kind", "Use hub_set, eligibility, or registration."));
        }
        else if ((request.Kind == HubSet) != (request.AllocationId is null))
        {
            errors.Add(new FieldError("allocation_id", request.Kind == HubSet
                ? "A hub_set check has no allocation."
                : "Send the allocation the check is for."));
        }

        if (request.Latitude is not (>= -90 and <= 90))
        {
            errors.Add(new FieldError("latitude", "Send a latitude from -90 to 90."));
        }

        if (request.Longitude is not (>= -180 and <= 180))
        {
            errors.Add(new FieldError("longitude", "Send a longitude from -180 to 180."));
        }

        if (request.AccuracyM is < 0)
        {
            errors.Add(new FieldError("accuracy_m", "Accuracy is a radius in metres, 0 or more."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (request.AllocationId is { } allocationId && !await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM allocations WHERE id = @allocationId AND published_at IS NOT NULL)", new { allocationId }))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Allocation not found", "No such allocation.");
        }

        // The position is a bound parameter: Postgres does not log those, and the function stores none of it.
        var recorded = await connection.QuerySingleAsync<Recorded>(
            """
            SELECT recorded AS WasRecorded, nearest_city_id AS NearestCityId, nearest_city_km AS NearestCityKm,
                   nearest_hub_km AS NearestHubKm, hub_match AS HubMatch
            FROM record_hub_location(@userId, @kind, @allocationId, @latitude::numeric, @longitude::numeric, @accuracy::numeric)
            """,
            new
            {
                userId,
                kind = request.Kind,
                allocationId = request.AllocationId,
                latitude = request.Latitude,
                longitude = request.Longitude,
                accuracy = request.AccuracyM,
            });
        if (!recorded.WasRecorded)
        {
            return Results.Ok(new LocationCheck(false, null, null, null, null));
        }

        var city = await connection.QuerySingleAsync<HubCity>(
            "SELECT id AS Id, name AS Name, region AS Region, country AS Country, time_zone AS TimeZone FROM cities WHERE id = @id",
            new { id = recorded.NearestCityId });
        return Results.Ok(new LocationCheck(true, recorded.HubMatch, city, recorded.NearestCityKm, recorded.NearestHubKm));
    }
}
