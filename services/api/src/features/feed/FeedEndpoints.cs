using Dapper;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Feed;

/// <summary>
/// openapi <c>GET /feed</c>, the home feed: shared photos (verified merch and concert photos) and public album logs
/// from every fan, newest first. Deleted accounts, deleted logs, and inactive artists are left out. Logs shared with
/// followers only stay out until follows exist (Phase 8).
/// </summary>
public static class FeedEndpoints
{
    public const int ReviewExcerptLength = 280;

    public static void MapFeed(this RouteGroupBuilder v1) => v1.MapGet("/feed", GetAsync);

    /// <summary>Where a concert photo was taken: the listed show (with its name) or the show the fan entered.</summary>
    public sealed record FeedConcert(string? Name, string? Venue, string? City, DateOnly Date);

    public sealed record FeedPhoto(
        string Kind, string? Caption, FeedConcert? Concert, IReadOnlyList<SubmissionEndpoints.SubmissionFile> Files);

    public sealed record FeedAlbumRef(Guid Id, string Title, string? CoverUrl);

    public sealed record FeedLog(FeedAlbumRef Album, int? Rating, string? Review, bool ReviewTruncated, bool IsRelisten, DateOnly ListenedOn);

    public sealed record FeedItem(
        string Type, Guid Id, DateTime At, SubmissionEndpoints.UserRef User, ScoringEndpoints.ArtistRef Artist, FeedPhoto? Photo,
        FeedLog? Log);

    public sealed record FeedPage(IReadOnlyList<FeedItem> Items, string? NextCursor);

    private static async Task<IResult> GetAsync(
        int? limit, string? cursor, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
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

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<FeedRow>(
            """
            SELECT item.*, names.* FROM (
              SELECT 'photo' AS Type, s.id AS Id, s.shared_at AS At, s.user_id AS UserId, s.artist_id AS ArtistId,
                     s.kind::text AS Kind, s.caption AS Caption,
                     e.name AS ConcertName, COALESCE(e.venue, s.concert_venue) AS ConcertVenue,
                     COALESCE(e.city, s.concert_city) AS ConcertCity,
                     COALESCE(e.starts_at::date, s.concert_date) AS ConcertDate,
                     NULL::uuid AS AlbumId, NULL::text AS AlbumTitle, NULL::text AS CoverUrl, NULL::smallint AS Rating,
                     NULL::text AS Review, false AS IsRelisten, NULL::date AS ListenedOn
              FROM submissions s LEFT JOIN events e ON e.id = s.event_id
              WHERE s.shared_at IS NOT NULL AND s.status = 'verified'
              UNION ALL
              SELECT 'album_log', l.id, l.created_at, l.user_id, al.artist_id, NULL, NULL,
                     NULL, NULL, NULL, NULL,
                     al.id, al.title, al.cover_url, l.rating, l.review, l.is_relisten, l.listened_on
              FROM album_logs l JOIN albums al ON al.id = l.album_id
              WHERE l.deleted_at IS NULL AND l.removed_at IS NULL AND l.visibility = 'public'
            ) item
            JOIN users u ON u.id = item.UserId AND u.deleted_at IS NULL
            JOIN artists a ON a.id = item.ArtistId AND a.status = 'active'
            CROSS JOIN LATERAL (SELECT u.handle::text AS Handle, u.display_name AS DisplayName,
                                       a.name AS ArtistName, a.image_url AS ArtistImageUrl) names
            ORDER BY item.At DESC, item.Id
            LIMIT @take OFFSET @offset
            """,
            new { take = pageSize + 1, offset })).ToList();

        var page = rows.Take(pageSize).ToList();
        var photoIds = page.Where(r => r.Type == "photo").Select(r => r.Id).ToArray();
        var files = (await connection.QueryAsync<(Guid SubmissionId, Guid Id, string MimeType, bool Redacted)>(
                "SELECT submission_id, id, mime_type, redacted FROM submission_files WHERE submission_id = ANY(@photoIds) ORDER BY uploaded_at, id",
                new { photoIds }))
            .ToLookup(f => f.SubmissionId);

        var items = page.Select(r => new FeedItem(
            r.Type, r.Id, r.At, new SubmissionEndpoints.UserRef(r.Handle, false, r.DisplayName, null),
            new ScoringEndpoints.ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl),
            r.Type == "photo"
                ? new FeedPhoto(r.Kind!, r.Caption,
                    r.ConcertDate is { } date ? new FeedConcert(r.ConcertName, r.ConcertVenue, r.ConcertCity, date) : null,
                    [.. files[r.Id].Select(f =>
                    new SubmissionEndpoints.SubmissionFile(f.Id, f.MimeType, UploadEndpoints.FileUrl(request, urls, f.Id), f.Redacted))])
                : null,
            r.Type == "album_log"
                ? new FeedLog(new FeedAlbumRef(r.AlbumId!.Value, r.AlbumTitle!, r.CoverUrl), r.Rating,
                    Excerpt(r.Review), r.Review is { Length: > ReviewExcerptLength }, r.IsRelisten, r.ListenedOn!.Value)
                : null)).ToList();
        return Results.Ok(new FeedPage(items, rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static string? Excerpt(string? review) =>
        string.IsNullOrWhiteSpace(review) ? null
        : review.Length <= ReviewExcerptLength ? review
        : review[..(ReviewExcerptLength - 1)].TrimEnd() + "…";

    private sealed record FeedRow(
        string Type, Guid Id, DateTime At, Guid UserId, Guid ArtistId, string? Kind, string? Caption, string? ConcertName,
        string? ConcertVenue, string? ConcertCity, DateOnly? ConcertDate, Guid? AlbumId,
        string? AlbumTitle, string? CoverUrl, short? Rating, string? Review, bool IsRelisten, DateOnly? ListenedOn,
        string Handle, string? DisplayName, string ArtistName, string? ArtistImageUrl);
}
