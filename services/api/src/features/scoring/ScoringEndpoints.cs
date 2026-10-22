using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Scoring;

/// <summary>
/// openapi <c>/me/scores</c>, <c>/artists/{id}/score</c>, its refresh, and the staff-only <c>/admin/scoring-rules</c>.
/// Scores are computed by the worker (<c>scoring.score_artist</c>); these endpoints read them and queue rescores.
/// Spotify weights in <c>scoring_rules</c> are relative shares; changing one queues a rescore of every active artist
/// (database trigger), so new weights reach every fan within minutes.
/// </summary>
public static class ScoringEndpoints
{
    public static void MapScoring(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/scores", ListMineAsync);
        v1.MapGet("/artists/{artistId:guid}/score", GetAsync);
        v1.MapPost("/artists/{artistId:guid}/score/refresh", RefreshAsync);

        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/scoring-rules", ListRulesAsync);
        admin.MapPut("/scoring-rules/{ruleKey}", UpdateRuleAsync);
    }

    public sealed record ArtistRef(Guid Id, string Name, string? ImageUrl);

    public sealed record ScoreSummary(
        Guid ArtistId, ArtistRef Artist, decimal Percentile, decimal RawScore, bool IsEstimated, DateTime ComputedAt);

    public sealed record ScoreSummaryPage(IReadOnlyList<ScoreSummary> Items, string? NextCursor);

    public sealed record Score(
        Guid ArtistId, string Status, decimal? Percentile, decimal? RawScore, bool IsEstimated, decimal? BaselineWeight,
        DateTime? ComputedAt, JsonElement? Breakdown);

    public sealed record ScoringRule(
        string RuleKey, decimal Points, int? DailyCapCount, bool Active, string? Description, decimal? Saturation);

    public sealed record ScoringRules(IReadOnlyList<ScoringRule> Items);

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal user, string? sort, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var orderBy = (sort ?? "percentile") switch
        {
            "percentile" => "s.percentile DESC, lower(a.name), a.id",
            "recent" => "s.computed_at DESC, a.id",
            _ => null,
        };
        if (orderBy is null)
        {
            errors.Add(new FieldError("sort", "Use percentile or recent."));
        }

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

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SummaryRow>(
            $"""
             SELECT a.id AS ArtistId, a.name AS Name, a.image_url AS ImageUrl, s.percentile AS Percentile,
                    s.raw_score AS RawScore, s.is_estimated AS IsEstimated, s.computed_at AS ComputedAt
             FROM user_artist_scores s JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
             WHERE s.user_id = @userId
             ORDER BY {orderBy}
             LIMIT @take OFFSET @offset
             """,
            new { userId = user.UserId(), take = pageSize + 1, offset })).ToList();

        return Results.Ok(new ScoreSummaryPage(
            [.. rows.Take(pageSize).Select(r => new ScoreSummary(r.ArtistId, new ArtistRef(r.ArtistId, r.Name, r.ImageUrl),
                r.Percentile, r.RawScore, r.IsEstimated, r.ComputedAt))],
            rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, Guid artistId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, user.UserId(), artistId));
    }

    private static async Task<IResult> RefreshAsync(
        ClaimsPrincipal user, Guid artistId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var score = await LoadAsync(connection, user.UserId(), artistId);
        if (score.Status == "artist_onboarding")
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "artist_not_active", "Artist is not active",
                "This artist is still being set up. Scores start once that finishes.");
        }

        // At most one rescore is queued per artist, so repeated taps cost nothing.
        await connection.ExecuteAsync("SELECT request_artist_rescore(@artistId)", new { artistId });
        return Results.Accepted($"/v1/artists/{artistId}/score", score);
    }

    private static async Task<Score> LoadAsync(NpgsqlConnection connection, Guid userId, Guid artistId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ScoreRow>(
            """
            SELECT a.status::text AS ArtistStatus, s.percentile AS Percentile, s.raw_score AS RawScore,
                   s.is_estimated AS IsEstimated, s.baseline_weight AS BaselineWeight, s.computed_at AS ComputedAt,
                   s.breakdown::text AS Breakdown
            FROM artists a
            LEFT JOIN user_artist_scores s ON s.artist_id = a.id AND s.user_id = @userId
            WHERE a.id = @artistId AND a.status IN ('onboarding', 'active')
            """,
            new { userId, artistId })
            ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Artist not found",
                "No tracked artist has this id.");

        if (row.ArtistStatus == "onboarding")
        {
            return new Score(artistId, "artist_onboarding", null, null, false, null, null, null);
        }

        if (row.ComputedAt is null)
        {
            return new Score(artistId, "computing", null, null, false, null, null, null);
        }

        using var breakdown = JsonDocument.Parse(row.Breakdown ?? "{}");
        return new Score(artistId, "ready", row.Percentile, row.RawScore, row.IsEstimated ?? false, row.BaselineWeight,
            row.ComputedAt, breakdown.RootElement.Clone());
    }

    private static async Task<IResult> ListRulesAsync(NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rules = await connection.QueryAsync<ScoringRule>($"SELECT {RuleColumns} FROM scoring_rules ORDER BY rule_key");
        return Results.Ok(new ScoringRules([.. rules]));
    }

    // Partial update: only the fields sent change. daily_cap_count may be sent as null to remove the cap.
    private static async Task<IResult> UpdateRuleAsync(
        string ruleKey, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var errors = new List<FieldError>();
        var points = ReadDecimal(body, "points", errors, min: 0, minExclusive: false);
        var saturation = ReadDecimal(body, "saturation", errors, min: 0, minExclusive: true);
        bool? active = null;
        if (body.TryGetProperty("active", out var activeValue))
        {
            if (activeValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                active = activeValue.GetBoolean();
            }
            else
            {
                errors.Add(new FieldError("active", "Send true or false."));
            }
        }

        var setCap = body.TryGetProperty("daily_cap_count", out var capValue);
        int? cap = null;
        if (setCap && capValue.ValueKind != JsonValueKind.Null)
        {
            if (capValue.ValueKind == JsonValueKind.Number && capValue.TryGetInt32(out var capInt) && capInt >= 1)
            {
                cap = capInt;
            }
            else
            {
                errors.Add(new FieldError("daily_cap_count", "Send a whole number of at least 1, or null for no cap."));
            }
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var isSpotify = ruleKey.StartsWith("spotify_", StringComparison.Ordinal);
        if (saturation is not null && !isSpotify)
        {
            errors.Add(new FieldError("saturation", "Only Spotify rules have a saturation."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var rule = await connection.QuerySingleOrDefaultAsync<ScoringRule>(
            $"""
             UPDATE scoring_rules
             SET points = COALESCE(@points, points), saturation = COALESCE(@saturation, saturation),
                 active = COALESCE(@active, active),
                 daily_cap_count = CASE WHEN @setCap THEN @cap ELSE daily_cap_count END
             WHERE rule_key = @ruleKey
             RETURNING {RuleColumns}
             """,
            new { ruleKey, points, saturation, active, setCap, cap })
            ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Rule not found",
                "No scoring rule has this key.");
        return Results.Ok(rule);
    }

    private static decimal? ReadDecimal(JsonElement body, string field, List<FieldError> errors, decimal min, bool minExclusive)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        // numeric(8,2) holds up to 999999.99.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            && (minExclusive ? number > min : number >= min) && number < 1_000_000)
        {
            return Math.Round(number, 2);
        }

        errors.Add(new FieldError(field, minExclusive
            ? $"Send a number greater than {min} and below 1000000."
            : $"Send a number of at least {min} and below 1000000."));
        return null;
    }

    private const string RuleColumns =
        "rule_key AS RuleKey, points AS Points, daily_cap_count AS DailyCapCount, active AS Active, description AS Description, saturation AS Saturation";

    private sealed record SummaryRow(
        Guid ArtistId, string Name, string? ImageUrl, decimal Percentile, decimal RawScore, bool IsEstimated, DateTime ComputedAt);

    private sealed record ScoreRow(
        string ArtistStatus, decimal? Percentile, decimal? RawScore, bool? IsEstimated, decimal? BaselineWeight,
        DateTime? ComputedAt, string? Breakdown);
}
