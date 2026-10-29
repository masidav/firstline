using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Links;

/// <summary>
/// Artist, merch, and ticket links (Phase 8, migration 0038). Staff set an artist's website and merch store (the worker
/// also fills the website from Ticketmaster when it links the artist); the app shows them with the artist's Spotify page
/// and each show's ticket link, and reports each tap here. Clicks are counted, anonymously, and never presented as
/// purchases.
/// </summary>
public static class LinkEndpoints
{
    public const int MaxUrlLength = 500;

    public static void MapLinks(this RouteGroupBuilder v1)
    {
        v1.MapPost("/clicks", RecordAsync).RequireRateLimiting(AbuseLimits.Churn);
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapPatch("/artists/{artistId:guid}/links", SetLinksAsync);
        admin.MapGet("/clicks", SummaryAsync);
    }

    public sealed record ArtistLinks(Guid ArtistId, string? WebsiteUrl, string? MerchUrl);

    public sealed record ClickCount(Guid ArtistId, string ArtistName, string Kind, int Clicks);

    public sealed record ClickSummary(int Days, IReadOnlyList<ClickCount> Items);

    private static readonly string[] Kinds = ["artist_website", "artist_merch", "artist_spotify", "event_tickets"];

    // A tap on an outbound link. Fire-and-forget from the app; 204 either way once valid.
    private static async Task<IResult> RecordAsync(JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        string? Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        var kind = Text("kind");
        var errors = new List<FieldError>();
        if (kind is null || !Kinds.Contains(kind))
        {
            errors.Add(new FieldError("kind", $"Use one of: {string.Join(", ", Kinds)}."));
        }

        if (!Guid.TryParse(Text("artist_id"), out var artistId))
        {
            errors.Add(new FieldError("artist_id", "Send the artist's id."));
        }

        Guid? eventId = Guid.TryParse(Text("event_id"), out var parsed) ? parsed : null;
        if ((kind == "event_tickets") != eventId.HasValue)
        {
            errors.Add(new FieldError("event_id", "Send the show's id for event_tickets, and only then."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var recorded = await connection.ExecuteAsync(
            """
            INSERT INTO link_clicks (kind, artist_id, event_id)
            SELECT @kind, a.id, @eventId FROM artists a
            WHERE a.id = @artistId AND a.status IN ('onboarding', 'active')
              AND (@eventId::uuid IS NULL OR EXISTS (SELECT 1 FROM events e WHERE e.id = @eventId AND e.artist_id = a.id))
            """,
            new { kind, artistId, eventId });
        if (recorded == 0)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No such artist or show.");
        }

        return Results.NoContent();
    }

    // Staff: set or clear an artist's website and merch store (https links). A missing field is left alone.
    private static async Task<IResult> SetLinksAsync(Guid artistId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var sets = new List<string>();
        var parameters = new DynamicParameters(new { artistId });
        var errors = new List<FieldError>();
        foreach (var (field, column) in new[] { ("website_url", "website_url"), ("merch_url", "merch_url") })
        {
            if (!body.TryGetProperty(field, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length == 0))
            {
                sets.Add($"{column} = NULL");
            }
            else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { } url
                     && url.Length <= MaxUrlLength && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                     && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Contains('.', StringComparison.Ordinal))
            {
                sets.Add($"{column} = @{column}");
                parameters.Add(column, url);
            }
            else
            {
                errors.Add(new FieldError(field, $"Use an https:// link of up to {MaxUrlLength} characters, or null to clear it."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (sets.Count > 0)
        {
            await connection.ExecuteAsync($"UPDATE artists SET {string.Join(", ", sets)} WHERE id = @artistId", parameters);
        }

        var links = await connection.QuerySingleOrDefaultAsync<ArtistLinks>(
            "SELECT id AS ArtistId, website_url AS WebsiteUrl, merch_url AS MerchUrl FROM artists WHERE id = @artistId", new { artistId })
            ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Artist not found", "No tracked artist has this id.");
        return Results.Ok(links);
    }

    // Staff: clicks per artist and link kind over the last `days` (1 to 365, default 30), most first.
    private static async Task<IResult> SummaryAsync(int? days, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 365)
        {
            throw ApiProblemException.Validation(new FieldError("days", "Use 1 to 365 days."));
        }

        var window = days ?? 30;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ClickCount>(
            """
            SELECT c.artist_id AS ArtistId, a.name AS ArtistName, c.kind AS Kind, count(*)::int AS Clicks
            FROM link_clicks c JOIN artists a ON a.id = c.artist_id
            WHERE c.clicked_at > now() - make_interval(days => @window)
            GROUP BY c.artist_id, a.name, c.kind
            ORDER BY count(*) DESC, a.name, c.kind
            LIMIT 200
            """,
            new { window });
        return Results.Ok(new ClickSummary(window, [.. rows]));
    }
}
