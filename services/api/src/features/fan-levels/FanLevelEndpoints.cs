using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using Npgsql;

namespace FandomScore.Api.Features.FanLevels;

/// <summary>
/// A fan's overall level, 1 to 5, from activity across the app (migration 0039): fandom scores, albums logged, verified
/// proof, trivia, and prompts, weighted equally. <c>GET /me/level</c> computes it fresh with each part, so the fan can
/// see what moves it. Other fans' levels show on their page and feed cards to the fan and their followers, like other
/// stats. The level never decides tickets. (Trust, the other overall score, is never shown.)
/// </summary>
public static class FanLevelEndpoints
{
    public const int Levels = 5;

    public static void MapFanLevels(this RouteGroupBuilder v1) => v1.MapGet("/me/level", GetMineAsync);

    public sealed record FanLevelPart(string Key, decimal Value);

    /// <summary><c>NextLevelAt</c>: the activity (0..1) the next level starts at; null at level 5.</summary>
    public sealed record FanLevel(int Level, int MaxLevel, decimal Activity, decimal? NextLevelAt, IReadOnlyList<FanLevelPart> Parts);

    private static async Task<IResult> GetMineAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<(string Parts, decimal Activity, short Level)>(
            """
            SELECT p::text, parts_average(p), fan_level_for(parts_average(p))
            FROM fan_activity_parts(@me) p
            """,
            new { me = user.UserId() });
        using var parts = JsonDocument.Parse(row.Parts);
        var list = parts.RootElement.EnumerateObject().Select(p => new FanLevelPart(p.Name, p.Value.GetDecimal())).ToList();
        decimal? next = row.Level >= Levels ? null : row.Level / (decimal)Levels;
        return Results.Ok(new FanLevel(row.Level, Levels, row.Activity, next, list));
    }
}
