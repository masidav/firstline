using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Follows;

/// <summary>
/// openapi tag <c>Social</c>: follow/follower relationships between fans (migration 0018, decided 2026-10-20).
/// Following needs no approval; a fan can unfollow, or remove one of their followers. Followers see the fan's
/// followers-only album logs, grab bag, and top fandom stats on the home feed. Blocking is not wanted for now.
/// </summary>
public static class FollowEndpoints
{
    public static void MapFollows(this RouteGroupBuilder v1)
    {
        v1.MapGet("/users", SearchAsync);
        v1.MapGet("/users/{handle}", GetProfileAsync);
        v1.MapPut("/users/{handle}/follow", FollowAsync).RequireRateLimiting(AbuseLimits.Churn);
        v1.MapDelete("/users/{handle}/follow", UnfollowAsync).RequireRateLimiting(AbuseLimits.Churn);
        v1.MapGet("/me/following", ListFollowingAsync);
        v1.MapGet("/me/followers", ListFollowersAsync);
        v1.MapDelete("/me/followers/{handle}", RemoveFollowerAsync);
    }

    /// <summary>openapi UserSummary: another fan as shown in lists, with how they and the caller relate.</summary>
    public sealed record UserSummary(
        string Handle, bool Deleted, string? DisplayName, string? AvatarUrl, bool FollowedByMe, bool FollowsMe);

    /// <summary>openapi UserPublic: a fan's public page.</summary>
    // Dapper maps records by constructor: the parameters are in the order the query selects the columns.
    /// <summary>
    /// <c>IsMe</c>: the caller's own page. <c>CanSeeFollowerContent</c>: the caller is the fan or follows them, so the
    /// grab bag, fandom stats, and followers-only posts are theirs to see. Band links only when <c>IsArtist</c>.
    /// </summary>
    public sealed record UserPublic(
        string Handle, bool Deleted, string? DisplayName, string? AvatarUrl, bool FollowedByMe, bool FollowsMe, DateTime CreatedAt,
        int LogCount, int ListCount, int FollowerCount, int FollowingCount, bool IsArtist, int GrabBagCount, bool IsMe,
        bool CanSeeFollowerContent, string? BandUrl, string? InstagramHandle, string? XHandle, int AnswerCount, int? FanLevel)
    {
        /// <summary>The level's name ("Front Row"), when the level is shown.</summary>
        public string? FanLevelName => FanLevel is { } level ? FanLevels.FanLevelEndpoints.Name(level) : null;
    }

    public sealed record UserPage(IReadOnlyList<UserSummary> Items, string? NextCursor);

    private const string SummaryColumns =
        """
        u.handle::text AS Handle, false AS Deleted, u.display_name AS DisplayName, u.avatar_url AS AvatarUrl,
        EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id) AS FollowedByMe,
        EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = u.id AND f.followee_id = @me) AS FollowsMe
        """;

    // Fans whose handle or display name starts with the query (or a word of it), the caller left out.
    private static async Task<IResult> SearchAsync(
        ClaimsPrincipal user, string? q, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length is < 1 or > 60)
        {
            errors.Add(new FieldError("q", "Send 1 to 60 characters to search for."));
        }

        var (take, offset) = ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var escaped = query.TrimStart('@').Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<UserSummary>(
            $"""
             SELECT {SummaryColumns} FROM users u
             WHERE u.deleted_at IS NULL AND u.id <> @me
               AND (u.handle::text ILIKE @prefix OR u.display_name ILIKE @prefix OR u.display_name ILIKE @wordPrefix)
             ORDER BY (u.handle::text ILIKE @prefix) DESC, lower(u.handle::text), u.id
             LIMIT @take OFFSET @offset
             """,
            new { me = user.UserId(), prefix = escaped + "%", wordPrefix = "% " + escaped + "%", take = take + 1, offset })).ToList();
        return Results.Ok(Page(rows, take, offset));
    }

    private static async Task<IResult> GetProfileAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var me = user.UserId();
        var profile = await connection.QuerySingleOrDefaultAsync<UserPublic>(
            $"""
             SELECT {SummaryColumns}, u.created_at AS CreatedAt,
                    (SELECT count(*) FROM album_logs l WHERE l.user_id = u.id AND l.deleted_at IS NULL AND l.removed_at IS NULL
                       AND (l.visibility = 'public' OR u.id = @me
                            OR (l.visibility = 'followers' AND EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id))))::int AS LogCount,
                    (SELECT count(*) FROM lists li WHERE li.user_id = u.id AND (li.visibility = 'public' OR u.id = @me
                       OR (li.visibility = 'followers' AND EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id))))::int AS ListCount,
                    (SELECT count(*) FROM follows f WHERE f.followee_id = u.id)::int AS FollowerCount,
                    (SELECT count(*) FROM follows f WHERE f.follower_id = u.id)::int AS FollowingCount,
                    u.is_artist AS IsArtist,
                    (SELECT count(*) FROM grab_bag_songs g WHERE g.user_id = u.id)::int AS GrabBagCount,
                    u.id = @me AS IsMe,
                    (u.id = @me OR EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id)) AS CanSeeFollowerContent,
                    u.band_url AS BandUrl, u.instagram_handle AS InstagramHandle, u.x_handle AS XHandle,
                    (SELECT count(*) FROM prompt_answers pa WHERE pa.user_id = u.id AND pa.deleted_at IS NULL AND pa.removed_at IS NULL
                       AND (pa.visibility = 'public' OR u.id = @me
                            OR EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id)))::int AS AnswerCount,
                    -- Fan level (0039), to the fan and followers like other stats; null before the first refresh.
                    CASE WHEN u.id = @me OR EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id)
                         THEN (SELECT fo.level FROM fan_overall_scores fo WHERE fo.user_id = u.id)::int END AS FanLevel
             FROM users u WHERE u.handle = @handle AND u.deleted_at IS NULL
             """,
            new { me, handle = CleanHandle(handle) }) ?? throw NotFound();
        return Results.Ok(profile);
    }

    private static async Task<IResult> FollowAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var target = await LiveUserAsync(connection, handle);
        if (target == me)
        {
            throw ApiProblemException.Validation(new FieldError("handle", "You can't follow yourself."));
        }

        // Idempotent: following twice is still following.
        await connection.ExecuteAsync(
            "INSERT INTO follows (follower_id, followee_id) VALUES (@me, @target) ON CONFLICT DO NOTHING", new { me, target });
        return Results.NoContent();
    }

    private static async Task<IResult> UnfollowAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM follows WHERE follower_id = @me AND followee_id = (SELECT id FROM users WHERE handle = @handle)",
            new { me = user.UserId(), handle = CleanHandle(handle) });
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFollowerAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM follows WHERE followee_id = @me AND follower_id = (SELECT id FROM users WHERE handle = @handle)",
            new { me = user.UserId(), handle = CleanHandle(handle) });
        return Results.NoContent();
    }

    private static Task<IResult> ListFollowingAsync(
        ClaimsPrincipal user, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken) =>
        ListAsync(user, limit, cursor, db, "f.follower_id = @me AND f.followee_id = u.id", cancellationToken);

    private static Task<IResult> ListFollowersAsync(
        ClaimsPrincipal user, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken) =>
        ListAsync(user, limit, cursor, db, "f.followee_id = @me AND f.follower_id = u.id", cancellationToken);

    // Newest follow first.
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, int? limit, string? cursor, NpgsqlDataSource db, string join, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<UserSummary>(
            $"""
             SELECT {SummaryColumns} FROM follows f JOIN users u ON {join}
             WHERE u.deleted_at IS NULL
             ORDER BY f.created_at DESC, u.id
             LIMIT @take OFFSET @offset
             """,
            new { me = user.UserId(), take = take + 1, offset })).ToList();
        return Results.Ok(Page(rows, take, offset));
    }

    private static (int Take, int Offset) ReadPage(int? limit, string? cursor, List<FieldError> errors)
    {
        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        return (limit ?? 25, offset);
    }

    private static UserPage Page(List<UserSummary> rows, int take, int offset) =>
        new([.. rows.Take(take)], rows.Count > take ? PageCursor.Write(offset + take) : null);

    /// <summary>The id of the live account with this handle (a leading @ is fine), or 404.</summary>
    public static async Task<Guid> LiveUserAsync(NpgsqlConnection connection, string handle) =>
        await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE handle = @handle AND deleted_at IS NULL", new { handle = CleanHandle(handle) })
        ?? throw NotFound();

    /// <summary>Whether <paramref name="follower"/> follows <paramref name="followee"/>.</summary>
    public static Task<bool> IsFollowingAsync(NpgsqlConnection connection, Guid follower, Guid followee) =>
        connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM follows WHERE follower_id = @follower AND followee_id = @followee)", new { follower, followee });

    private static string CleanHandle(string handle) => handle.Trim().TrimStart('@');

    private static ApiProblemException NotFound() =>
        new(StatusCodes.Status404NotFound, "not_found", "Not found", "No account has this handle.");
}
