using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Sharing;

/// <summary>
/// Score cards (Phase 8): a shareable image of the caller's fandom score for an artist. <c>GET /me/scores/{id}/card</c>
/// returns a short-lived signed link to the PNG (so the app can download it without a token, like photos) and the
/// sentence for the share sheet. The image is drawn when fetched, from the current score.
/// </summary>
public static class ScoreCardEndpoints
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static void MapScoreCards(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/scores/{artistId:guid}/card", GetAsync);
        v1.MapGet("/score-cards/{userId:guid}/{artistId:guid}.png", ImageAsync).AllowAnonymous();
    }

    public sealed record ScoreCardLink(string ImageUrl, string ShareText, string TopLabel, bool IsEstimated, DateTime ExpiresAt);

    // The purpose carries the artist, the signed id is the fan: a link opens one fan's card for one artist.
    private static string Purpose(Guid artistId) => $"score-card:{artistId}";

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, Guid artistId, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var card = await LoadAsync(connection, me, artistId, time) ?? throw NoScore();
        var (query, expiresAt) = urls.Sign(Purpose(artistId), me, LinkLifetime);
        var url = $"{SignedUrls.Origin(request)}/v1/score-cards/{me}/{artistId}.png{query}";
        return Results.Ok(new ScoreCardLink(url, ScoreCard.ShareText(card), ScoreCard.TopLabel(card.Percentile), card.IsEstimated,
            expiresAt.UtcDateTime));
    }

    private static async Task<IResult> ImageAsync(
        Guid userId, Guid artistId, long? exp, string? sig, NpgsqlDataSource db, SignedUrls urls, TimeProvider time,
        HttpResponse response, CancellationToken cancellationToken)
    {
        if (!urls.Verify(Purpose(artistId), userId, exp, sig))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Link expired", "This link is invalid or has expired.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var card = await LoadAsync(connection, userId, artistId, time) ?? throw NoScore();
        response.Headers.CacheControl = "private, max-age=300";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(ScoreCard.Render(card), "image/png");
    }

    // Only an active artist the fan has a score for; a deleted account has no card.
    private static async Task<ScoreCardContent?> LoadAsync(NpgsqlConnection connection, Guid userId, Guid artistId, TimeProvider time)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string ArtistName, decimal Percentile, bool IsEstimated, string? Handle)>(
            """
            SELECT a.name, s.percentile, s.is_estimated, u.handle::text
            FROM user_artist_scores s
            JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
            JOIN users u ON u.id = s.user_id AND u.deleted_at IS NULL
            WHERE s.user_id = @userId AND s.artist_id = @artistId
            """,
            new { userId, artistId });
        return row.ArtistName is null
            ? null
            : new ScoreCardContent(row.ArtistName, row.Percentile, row.IsEstimated, row.Handle, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
    }

    private static ApiProblemException NoScore() =>
        new(StatusCodes.Status404NotFound, "not_found", "No score", "There's no fandom score for this artist to share yet.");
}
