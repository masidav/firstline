using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Reactions;

/// <summary>
/// Emoji reactions on feed posts (migration 0026, decided 2026-10-25: reactions instead of comments). A fan can react
/// to a shared photo, an album log, or a prompt answer they can see, with any of a fixed set of emoji (several
/// different ones per post), and take a reaction back. No points.
/// </summary>
public static class ReactionEndpoints
{
    /// <summary>Reaction keys; the app maps them to emoji (custom emoji can map to images later).</summary>
    public static readonly IReadOnlyList<string> Reactions = ["fire", "heart", "laugh", "rock", "wow", "clap"];

    public static readonly IReadOnlyList<string> TargetTypes = ["photo", "log", "prompt_answer"];

    public static void MapReactions(this RouteGroupBuilder v1)
    {
        v1.MapPut("/reactions/{targetType}/{targetId:guid}/{reaction}", AddAsync);
        v1.MapDelete("/reactions/{targetType}/{targetId:guid}/{reaction}", RemoveAsync);
    }

    /// <summary>openapi ReactionCount: how many fans reacted with this emoji, and whether the caller did.</summary>
    public sealed record ReactionCount(string Reaction, int Count, bool Mine);

    public sealed record ReactionSummary(IReadOnlyList<ReactionCount> Reactions);

    private static async Task<IResult> AddAsync(
        ClaimsPrincipal user, string targetType, Guid targetId, string reaction, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        Validate(targetType, reaction);
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireVisibleAsync(connection, me, targetType, targetId);
        await connection.ExecuteAsync(
            """
            INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES (@targetType, @targetId, @me, @reaction)
            ON CONFLICT DO NOTHING
            """,
            new { targetType, targetId, me, reaction });
        return Results.Ok(new ReactionSummary((await LoadAsync(connection, me, targetType, [targetId])).GetValueOrDefault(targetId) ?? []));
    }

    private static async Task<IResult> RemoveAsync(
        ClaimsPrincipal user, string targetType, Guid targetId, string reaction, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        Validate(targetType, reaction);
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM post_reactions WHERE target_type = @targetType AND target_id = @targetId AND user_id = @me AND reaction = @reaction",
            new { targetType, targetId, me, reaction });
        return Results.Ok(new ReactionSummary((await LoadAsync(connection, me, targetType, [targetId])).GetValueOrDefault(targetId) ?? []));
    }

    /// <summary>
    /// Reaction counts for posts of one type, by post id, in the fixed emoji order; the caller's own marked. Only live
    /// accounts count.
    /// </summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<ReactionCount>>> LoadAsync(
        NpgsqlConnection connection, Guid me, string targetType, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<(Guid TargetId, string Reaction, int Count, bool Mine)>(
            """
            SELECT r.target_id, r.reaction, count(*)::int, bool_or(r.user_id = @me)
            FROM post_reactions r JOIN users u ON u.id = r.user_id AND u.deleted_at IS NULL
            WHERE r.target_type = @targetType AND r.target_id = ANY(@ids)
            GROUP BY r.target_id, r.reaction
            """,
            new { me, targetType, ids });
        return rows.GroupBy(r => r.TargetId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ReactionCount>)[.. g.OrderBy(r => IndexOf(r.Reaction)).Select(r => new ReactionCount(r.Reaction, r.Count, r.Mine))]);
    }

    private static int IndexOf(string reaction)
    {
        for (var i = 0; i < Reactions.Count; i++)
        {
            if (Reactions[i] == reaction)
            {
                return i;
            }
        }

        return Reactions.Count;
    }

    private static void Validate(string targetType, string reaction)
    {
        if (!TargetTypes.Contains(targetType))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "Use photo, log, or prompt_answer.");
        }

        if (!Reactions.Contains(reaction))
        {
            throw ApiProblemException.Validation(new FieldError("reaction", $"Use one of: {string.Join(", ", Reactions)}."));
        }
    }

    // The same rule as the feed: shared photos, logs and prompt answers the caller may see (their own included).
    private static async Task RequireVisibleAsync(NpgsqlConnection connection, Guid me, string targetType, Guid targetId)
    {
        var visible = await connection.ExecuteScalarAsync<bool>(
            targetType switch
            {
                "photo" => """
                    SELECT EXISTS (SELECT 1 FROM submissions s JOIN users u ON u.id = s.user_id AND u.deleted_at IS NULL
                     WHERE s.id = @targetId AND s.status = 'verified' AND s.removed_at IS NULL AND (s.shared_at IS NOT NULL OR s.user_id = @me))
                    """,
                "log" => """
                    SELECT EXISTS (SELECT 1 FROM album_logs l JOIN users u ON u.id = l.user_id AND u.deleted_at IS NULL
                     WHERE l.id = @targetId AND l.deleted_at IS NULL AND l.removed_at IS NULL
                       AND (l.user_id = @me OR l.visibility = 'public'
                            OR (l.visibility = 'followers' AND EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = l.user_id))))
                    """,
                _ => """
                    SELECT EXISTS (SELECT 1 FROM prompt_answers pa JOIN users u ON u.id = pa.user_id AND u.deleted_at IS NULL
                     WHERE pa.id = @targetId AND pa.deleted_at IS NULL AND pa.removed_at IS NULL
                       AND (pa.user_id = @me OR pa.visibility = 'public'
                            OR EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = pa.user_id)))
                    """,
            },
            new { me, targetId });
        if (!visible)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Post not found", "This post isn't available.");
        }
    }
}
