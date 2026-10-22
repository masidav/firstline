using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Users;

/// <summary>openapi Hub city: one of the curated metro areas a fan can pick.</summary>
public sealed record HubCity(string Id, string Name, string Region, string Country);

/// <summary>
/// openapi <c>GET /cities</c> and <c>PUT /me/hub-cities</c>. A fan has a primary hub city and up to two backups;
/// allocations put fans whose hubs include the show's city first. <c>set_hub_cities()</c> (migration 0010) enforces
/// the list rules and the once-a-week change limit; the first choice doesn't count as a change.
/// </summary>
public static class HubCities
{
    public static void MapHubCities(this RouteGroupBuilder v1)
    {
        v1.MapGet("/cities", SearchAsync);
        v1.MapPut("/me/hub-cities", SetAsync);
    }

    public sealed record HubCityList(IReadOnlyList<HubCity> Items);

    private const string Columns = "c.id AS Id, c.name AS Name, c.region AS Region, c.country AS Country";

    // Matches the start of the name or of any word in it ("york" finds New York), or the exact region code ("ON").
    private static async Task<IResult> SearchAsync(string? q, int? limit, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length > 100)
        {
            errors.Add(new FieldError("q", "Send at most 100 characters."));
        }

        if (limit is < 1 or > 200)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 200."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var escaped = query.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var cities = await connection.QueryAsync<HubCity>(
            $"""
             SELECT {Columns} FROM cities c
             WHERE c.active
               AND (@query = '' OR c.name ILIKE @prefix OR c.name ILIKE @wordPrefix OR c.region = upper(@query))
             ORDER BY lower(c.name), c.region
             LIMIT @take
             """,
            new { query, prefix = escaped + "%", wordPrefix = "% " + escaped + "%", take = limit ?? 20 });
        return Results.Ok(new HubCityList([.. cities]));
    }

    private static async Task<IResult> SetAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("city_ids", out var ids)
            || ids.ValueKind != JsonValueKind.Array || ids.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
        {
            throw ApiProblemException.Validation(new FieldError("city_ids", "Send city_ids: a list of city ids, primary first."));
        }

        var cityIds = ids.EnumerateArray().Select(i => i.GetString()!).ToArray();
        if (cityIds.Length is < 1 or > 3)
        {
            throw ApiProblemException.Validation(new FieldError("city_ids", "Choose a primary hub city and up to two backups."));
        }

        if (cityIds.Distinct(StringComparer.Ordinal).Count() != cityIds.Length)
        {
            throw ApiProblemException.Validation(new FieldError("city_ids", "Each city can only be chosen once."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync("SELECT set_hub_cities(@userId, @cityIds)", new { userId, cityIds });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText.StartsWith("hub cities: unknown city", StringComparison.Ordinal))
        {
            throw ApiProblemException.Validation(new FieldError("city_ids", "Pick cities from the list."));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText.StartsWith("hub change limit", StringComparison.Ordinal))
        {
            var next = await connection.ExecuteScalarAsync<DateTime>(
                "SELECT hubs_changed_at + interval '7 days' FROM users WHERE id = @userId", new { userId });
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "hub_change_limit", "Hub cities changed recently",
                $"Hub cities can be changed once a week. The next change is possible after {next:yyyy-MM-dd HH:mm} UTC.")
            {
                RetryAfter = next - time.GetUtcNow().UtcDateTime,
            };
        }

        return Results.Ok(await UserProfiles.LoadMeAsync(connection, userId)
                          ?? throw ApiProblemException.Unauthorized("The account no longer exists."));
    }

    /// <summary>The fan's hub cities, primary first, and when they may next change them (null: any time).</summary>
    public static async Task<(IReadOnlyList<HubCity> Cities, DateTime? ChangeAvailableAt)> LoadAsync(
        NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        var cities = await connection.QueryAsync<HubCity>(
            $"SELECT {Columns} FROM user_hub_cities h JOIN cities c ON c.id = h.city_id WHERE h.user_id = @userId ORDER BY h.position",
            new { userId }, transaction);
        var next = await connection.ExecuteScalarAsync<DateTime?>(
            """
            SELECT hubs_changed_at + interval '7 days' FROM users
            WHERE id = @userId AND hubs_changed_at + interval '7 days' > now()
            """,
            new { userId }, transaction);
        return ([.. cities], next);
    }
}
