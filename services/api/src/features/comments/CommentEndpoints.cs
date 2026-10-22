using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Dapper;
using FandomScore.Api.Features.AlbumJournal;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;

namespace FandomScore.Api.Features.Comments;

/// <summary>openapi Comment. A deleted comment that still has live replies is a tombstone: <see cref="Deleted"/> with no body.</summary>
public sealed record Comment(
    Guid Id, Guid LogId, UserRef User, Guid? ParentId, string? Body, bool Deleted, DateTime CreatedAt, DateTime? EditedAt);

public sealed record CommentResult(Comment Comment, IReadOnlyList<PointsAward> PointsAwarded);

public sealed record CommentPage(IReadOnlyList<Comment> Items, string? NextCursor);

/// <summary>
/// openapi tag Comments: comments and replies on album logs. A comment is visible exactly when its log is. Points
/// (<c>comment_posted</c>) come from the <c>comments_award</c> trigger: 20+ characters, on someone else's log, within
/// the daily cap; soft deletion reverses them (<c>comments_reverse</c>). Editing never changes points.
/// </summary>
public static class CommentEndpoints
{
    public const string PostRateLimit = "comment-post";
    public const int BodyMaxLength = 2000;
    private const int PostsPerHour = 60;

    public static IServiceCollection AddComments(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(limiter => limiter.AddPolicy(PostRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PostsPerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 })));

    public static void MapComments(this RouteGroupBuilder v1)
    {
        v1.MapGet("/logs/{logId:guid}/comments", ListAsync);
        v1.MapPost("/logs/{logId:guid}/comments", CreateAsync).RequireRateLimiting(PostRateLimit);
        v1.MapPatch("/comments/{commentId:guid}", UpdateAsync);
        v1.MapDelete("/comments/{commentId:guid}", DeleteAsync);
    }

    private const string Select =
        """
        SELECT c.id AS Id, c.album_log_id AS LogId, c.parent_id AS ParentId,
               CASE WHEN c.deleted_at IS NULL THEN c.body END AS Body, (c.deleted_at IS NOT NULL) AS Deleted,
               c.created_at AS CreatedAt, c.edited_at AS EditedAt, c.user_id AS UserId,
               u.handle::text AS UserHandle, (u.deleted_at IS NOT NULL) AS UserDeleted, u.display_name AS UserDisplayName,
               u.avatar_url AS UserAvatarUrl
        FROM comments c JOIN users u ON u.id = c.user_id
        """;

    private sealed record Row(
        Guid Id, Guid LogId, Guid? ParentId, string? Body, bool Deleted, DateTime CreatedAt, DateTime? EditedAt, Guid UserId,
        string? UserHandle, bool UserDeleted, string? UserDisplayName, string? UserAvatarUrl);

    private static Comment ToComment(Row r) =>
        new(r.Id, r.LogId, new UserRef(r.UserDeleted ? null : r.UserHandle, r.UserDeleted, r.UserDisplayName, r.UserAvatarUrl),
            r.ParentId, r.Body, r.Deleted, r.CreatedAt, r.EditedAt);

    // Oldest first; deleted comments stay only as tombstones while they have live replies.
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, Guid logId, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireLogVisibleAsync(connection, null, logId, user.UserId());
        var rows = (await connection.QueryAsync<Row>(
            $"""
             {Select}
             WHERE c.album_log_id = @logId
               AND (c.deleted_at IS NULL OR EXISTS (SELECT 1 FROM comments r WHERE r.parent_id = c.id AND r.deleted_at IS NULL))
             ORDER BY c.created_at, c.id
             LIMIT @take OFFSET @offset
             """,
            new { logId, take = take + 1, offset })).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new CommentPage([.. items.Select(ToComment)], next));
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user, HttpRequest request, Guid logId, JsonElement body, NpgsqlDataSource db, IOptions<JsonOptions> json,
        CancellationToken cancellationToken)
    {
        var key = Idempotency.ReadKey(request);
        var text = ParseBody(body, out var parentId);
        var userId = user.UserId();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (key is not null
            && await Idempotency.BeginAsync(connection, transaction, userId, key, Idempotency.Hash(body.GetRawText())) is { } replay)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay.ToResult();
        }

        await RequireLogVisibleAsync(connection, transaction, logId, userId);
        if (parentId is { } parent && !await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM comments WHERE id = @parent AND album_log_id = @logId AND deleted_at IS NULL)",
                new { parent, logId }, transaction))
        {
            throw ApiProblemException.Validation(new FieldError("parent_id", "No such comment on this log."));
        }

        var commentId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO comments (album_log_id, user_id, parent_id, body) VALUES (@logId, @userId, @parentId, @text) RETURNING id",
            new { logId, userId, parentId, text }, transaction);
        var result = new CommentResult(await LoadAsync(connection, transaction, commentId),
            await Journal.AwardsAsync(connection, transaction, "comment", commentId, afterId: 0));
        if (key is not null)
        {
            await Idempotency.CompleteAsync(connection, transaction, userId, key, StatusCodes.Status201Created,
                JsonSerializer.Serialize(result, json.Value.SerializerOptions));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/v1/comments/{commentId}", result);
    }

    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal user, Guid commentId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var text = ParseBody(body, out var parentId);
        if (parentId is not null)
        {
            throw ApiProblemException.Validation(new FieldError("parent_id", "A reply cannot be moved."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, commentId, user.UserId(), "Only the comment's author can edit it.");
        await connection.ExecuteAsync("UPDATE comments SET body = @text, edited_at = now() WHERE id = @commentId",
            new { text, commentId }, transaction);
        var comment = await LoadAsync(connection, transaction, commentId);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(comment);
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, Guid commentId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, commentId, user.UserId(), "Only the comment's author can delete it.");
        await connection.ExecuteAsync("UPDATE comments SET deleted_at = now() WHERE id = @commentId AND deleted_at IS NULL",
            new { commentId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The body text, trimmed, and an optional parent id. Validation errors are 422.</summary>
    private static string ParseBody(JsonElement body, out Guid? parentId)
    {
        parentId = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var errors = new List<FieldError>();
        var text = body.TryGetProperty("body", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
        if (text.Length == 0 || text.EnumerateRunes().Count() > BodyMaxLength)
        {
            errors.Add(new FieldError("body", $"Use 1 to {BodyMaxLength} characters."));
        }

        if (body.TryGetProperty("parent_id", out var parent) && parent.ValueKind != JsonValueKind.Null)
        {
            if (parent.ValueKind == JsonValueKind.String && Guid.TryParse(parent.GetString(), out var id))
            {
                parentId = id;
            }
            else
            {
                errors.Add(new FieldError("parent_id", "Send the id of the comment you are replying to."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        return text;
    }

    private static async Task<Comment> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid commentId) =>
        ToComment(await connection.QuerySingleAsync<Row>($"{Select} WHERE c.id = @commentId", new { commentId }, transaction));

    // A comment is visible exactly when its log is (openapi: private content is 404 to everyone but the owner).
    private static async Task RequireLogVisibleAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid logId, Guid viewer)
    {
        if (!await connection.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM album_logs l WHERE l.id = @logId AND l.deleted_at IS NULL AND {Journal.VisibleToViewer})",
                new { logId, viewer }, transaction))
        {
            throw Journal.NotFound("Log");
        }
    }

    // 404 when the comment is gone or on a log the caller can't see, 403 when visible but someone else's.
    private static async Task RequireOwnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid commentId, Guid userId, string forbiddenDetail)
    {
        var owner = await connection.QuerySingleOrDefaultAsync<(Guid UserId, bool Visible)?>(
            """
            SELECT c.user_id, (l.user_id = @viewer OR l.visibility = 'public') AS visible
            FROM comments c JOIN album_logs l ON l.id = c.album_log_id
            WHERE c.id = @commentId AND c.deleted_at IS NULL AND l.deleted_at IS NULL
            FOR UPDATE OF c
            """,
            new { commentId, viewer = userId }, transaction);
        if (owner is null || (owner.Value.UserId != userId && !owner.Value.Visible))
        {
            throw Journal.NotFound("Comment");
        }

        if (owner.Value.UserId != userId)
        {
            throw Journal.Forbidden(forbiddenDetail);
        }
    }
}
