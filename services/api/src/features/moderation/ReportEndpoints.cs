using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Moderation;

/// <summary>
/// openapi <c>POST /reports</c> and the staff queue <c>/admin/reports</c>. Fans report a shared photo or a public album
/// log; staff take the post down or dismiss the reports, which closes every open report on it and records the
/// decision in <c>moderation_actions</c>. Takedown effects are database triggers (migration 0014).
/// </summary>
public static class ReportEndpoints
{
    public static readonly IReadOnlyList<string> TargetTypes = ["photo", "log"];
    public static readonly IReadOnlyList<string> Reasons = ["spam", "abuse", "impersonation", "inappropriate", "other"];
    public const int DailyReports = 30;

    public static void MapReports(this RouteGroupBuilder v1)
    {
        v1.MapPost("/reports", CreateAsync);
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/reports", QueueAsync);
        admin.MapPost("/reports/{targetType}/{targetId:guid}/decision", DecideAsync);
    }

    public sealed record ReasonCount(string Reason, int Count);

    public sealed record ReportedPhoto(string Kind, string? Caption, IReadOnlyList<SubmissionEndpoints.SubmissionFile> Files);

    public sealed record ReportedLog(Guid AlbumId, string AlbumTitle, int? Rating, string? Review, string Visibility);

    public sealed record ReportedPost(
        string TargetType, Guid TargetId, SubmissionEndpoints.UserRef Author, ScoringEndpoints.ArtistRef Artist,
        ReportedPhoto? Photo, ReportedLog? Log, int ReportCount, IReadOnlyList<ReasonCount> Reasons, IReadOnlyList<string> Details,
        DateTime FirstReportedAt);

    public sealed record ReportedPostPage(IReadOnlyList<ReportedPost> Items, string? NextCursor);

    public sealed record Decision(string TargetType, Guid TargetId, string Action, int ReportsResolved);

    private static async Task<IResult> CreateAsync(ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        string? Text(string name) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var errors = new List<FieldError>();
        var targetType = Text("target_type");
        if (targetType is null || !TargetTypes.Contains(targetType))
        {
            errors.Add(new FieldError("target_type", "Use photo or log."));
        }

        if (!Guid.TryParse(Text("target_id"), out var targetId))
        {
            errors.Add(new FieldError("target_id", "Send the post's id."));
        }

        var reason = Text("reason");
        if (reason is null || !Reasons.Contains(reason))
        {
            errors.Add(new FieldError("reason", "Use spam, abuse, impersonation, inappropriate, or other."));
        }

        var details = Text("details")?.Trim();
        if (details is { Length: > 1000 })
        {
            errors.Add(new FieldError("details", "Use up to 1000 characters."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        // Only what the reporter can see on the feed can be reported: a shared photo, or a live log that is public
        // or followers-only by someone the reporter follows.
        var author = await connection.ExecuteScalarAsync<Guid?>(
            targetType == "photo"
                ? "SELECT user_id FROM submissions WHERE id = @targetId AND shared_at IS NOT NULL"
                : """
                  SELECT user_id FROM album_logs l WHERE id = @targetId AND deleted_at IS NULL AND removed_at IS NULL
                   AND (visibility = 'public' OR (visibility = 'followers'
                        AND EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @userId AND f.followee_id = l.user_id)))
                  """,
            new { targetId, userId });
        if (author is null)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Post not found", "This post isn't available to report.");
        }

        if (author == userId)
        {
            throw ApiProblemException.Validation(new FieldError("target_id", "You can't report your own post."));
        }

        if (await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM reports WHERE reporter_id = @userId AND created_at >= now() - interval '1 day'", new { userId })
            >= DailyReports)
        {
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many reports",
                $"You can send {DailyReports} reports a day.") { RetryAfter = TimeSpan.FromHours(1) };
        }

        // Reporting the same post again while it's open changes nothing.
        await connection.ExecuteAsync(
            """
            INSERT INTO reports (reporter_id, target_type, target_id, reason, details)
            VALUES (@userId, @targetType, @targetId, @reason, @details)
            ON CONFLICT (reporter_id, target_type, target_id) WHERE resolved_at IS NULL DO NOTHING
            """,
            new { userId, targetType, targetId, reason, details = string.IsNullOrEmpty(details) ? null : details });
        return Results.Accepted();
    }

    private static async Task<IResult> QueueAsync(
        int? limit, string? cursor, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw ApiProblemException.Validation(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            throw ApiProblemException.Validation(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var targets = (await connection.QueryAsync<(string TargetType, Guid TargetId, int Count, DateTime First)>(
            """
            SELECT target_type, target_id, count(*)::int, min(created_at)
            FROM reports WHERE resolved_at IS NULL
            GROUP BY target_type, target_id
            ORDER BY min(created_at), target_id
            LIMIT @take OFFSET @offset
            """,
            new { take = pageSize + 1, offset })).ToList();

        var items = new List<ReportedPost>();
        foreach (var t in targets.Take(pageSize))
        {
            if (await LoadAsync(connection, request, urls, t.TargetType, t.TargetId, t.Count, t.First) is { } post)
            {
                items.Add(post);
            }
        }

        return Results.Ok(new ReportedPostPage(items, targets.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<ReportedPost?> LoadAsync(
        NpgsqlConnection connection, HttpRequest request, SignedUrls urls, string targetType, Guid targetId, int count, DateTime first)
    {
        var reasons = (await connection.QueryAsync<ReasonCount>(
            """
            SELECT reason AS Reason, count(*)::int AS Count FROM reports
            WHERE target_type = @targetType AND target_id = @targetId AND resolved_at IS NULL
            GROUP BY reason ORDER BY count(*) DESC, reason
            """,
            new { targetType, targetId })).ToList();
        var details = (await connection.QueryAsync<string>(
            """
            SELECT details FROM reports
            WHERE target_type = @targetType AND target_id = @targetId AND resolved_at IS NULL AND details IS NOT NULL
            ORDER BY created_at DESC LIMIT 5
            """,
            new { targetType, targetId })).ToList();

        if (targetType == "photo")
        {
            var photo = await connection.QuerySingleOrDefaultAsync<PhotoRow>(
                """
                SELECT s.kind::text AS Kind, s.caption AS Caption, u.handle::text AS Handle, u.display_name AS DisplayName,
                       u.deleted_at IS NOT NULL AS Deleted, a.id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl
                FROM submissions s JOIN users u ON u.id = s.user_id JOIN artists a ON a.id = s.artist_id WHERE s.id = @targetId
                """,
                new { targetId });
            if (photo is null) return null;
            var files = (await connection.QueryAsync<(Guid Id, string MimeType, bool Redacted)>(
                    "SELECT id, mime_type, redacted FROM submission_files WHERE submission_id = @targetId ORDER BY uploaded_at, id", new { targetId }))
                .Select(f => new SubmissionEndpoints.SubmissionFile(f.Id, f.MimeType, UploadEndpoints.FileUrl(request, urls, f.Id), f.Redacted)).ToList();
            return new ReportedPost(targetType, targetId, Author(photo.Handle, photo.DisplayName, photo.Deleted),
                new ScoringEndpoints.ArtistRef(photo.ArtistId, photo.ArtistName, photo.ArtistImageUrl),
                new ReportedPhoto(photo.Kind, photo.Caption, files), null, count, reasons, details, first);
        }

        var log = await connection.QuerySingleOrDefaultAsync<LogRow>(
            """
            SELECT al.id AS AlbumId, al.title AS AlbumTitle, l.rating AS Rating, l.review AS Review, l.visibility::text AS Visibility,
                   u.handle::text AS Handle, u.display_name AS DisplayName, u.deleted_at IS NOT NULL AS Deleted,
                   a.id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl
            FROM album_logs l JOIN albums al ON al.id = l.album_id JOIN artists a ON a.id = al.artist_id JOIN users u ON u.id = l.user_id
            WHERE l.id = @targetId
            """,
            new { targetId });
        if (log is null) return null;
        return new ReportedPost(targetType, targetId, Author(log.Handle, log.DisplayName, log.Deleted),
            new ScoringEndpoints.ArtistRef(log.ArtistId, log.ArtistName, log.ArtistImageUrl), null,
            new ReportedLog(log.AlbumId, log.AlbumTitle, log.Rating, log.Review, log.Visibility), count, reasons, details, first);
    }

    private static SubmissionEndpoints.UserRef Author(string? handle, string? displayName, bool deleted) =>
        new(deleted ? null : handle, deleted, deleted ? null : displayName, null);

    private static async Task<IResult> DecideAsync(
        ClaimsPrincipal user, string targetType, Guid targetId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        string? Text(string name) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        if (!TargetTypes.Contains(targetType))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "Use photo or log.");
        }

        var action = Text("action");
        var note = Text("note")?.Trim();
        var errors = new List<FieldError>();
        if (action is not ("take_down" or "dismiss"))
        {
            errors.Add(new FieldError("action", "Use take_down or dismiss."));
        }

        if (note is { Length: > 2000 })
        {
            errors.Add(new FieldError("note", "Use up to 2000 characters."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var resolved = await connection.ExecuteAsync(
            """
            UPDATE reports SET resolved_at = now(), resolution = @resolution
            WHERE target_type = @targetType AND target_id = @targetId AND resolved_at IS NULL
            """,
            new { targetType, targetId, resolution = action == "take_down" ? "taken_down" : "dismissed" }, transaction);
        if (resolved == 0)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "No open reports",
                "This post has no open reports (someone may have decided it already).");
        }

        if (action == "take_down")
        {
            await connection.ExecuteAsync(
                targetType == "photo"
                    ? "UPDATE submissions SET removed_at = now() WHERE id = @targetId AND removed_at IS NULL"
                    : "UPDATE album_logs SET removed_at = now() WHERE id = @targetId AND removed_at IS NULL",
                new { targetId }, transaction);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO moderation_actions (staff_id, target_type, target_id, action, note, reports)
            VALUES (@staffId, @targetType, @targetId, @action, @note, @resolved)
            """,
            new { staffId = user.UserId(), targetType, targetId, action, note = string.IsNullOrEmpty(note) ? null : note, resolved },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new Decision(targetType, targetId, action!, resolved));
    }

    private sealed record PhotoRow(
        string Kind, string? Caption, string? Handle, string? DisplayName, bool Deleted, Guid ArtistId, string ArtistName, string? ArtistImageUrl);

    private sealed record LogRow(
        Guid AlbumId, string AlbumTitle, short? Rating, string? Review, string Visibility, string? Handle, string? DisplayName, bool Deleted,
        Guid ArtistId, string ArtistName, string? ArtistImageUrl);
}
