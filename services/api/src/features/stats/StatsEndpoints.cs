using System.Security.Claims;
using Dapper;
using FandomScore.Api.Infrastructure;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using Npgsql;

namespace FandomScore.Api.Features.Stats;

/// <summary>
/// openapi <c>GET /me/stats</c>: the signed-in fan's numbers in one response, read from stored state and the ledger
/// (never from Spotify). Deleted logs and comments and reversed awards don't count; listening counts cover active
/// tracked artists only. Other fans' stats wait on follows, blocks, and profile visibility (Phase 8).
/// </summary>
public static class StatsEndpoints
{
    public static void MapStats(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/stats", GetMineAsync);
        v1.MapGet("/users/{handle}/stats", GetForUserAsync);
    }

    /// <summary>
    /// openapi ProfileStats: another fan's fandom stats as their followers see them (decided 2026-10-20: followers see
    /// top fandom stats). Listening counts stay private to the fan.
    /// </summary>
    public sealed record ProfileStats(IReadOnlyList<ArtistStat> Artists, JournalStats Journal, ProofStats Proof, decimal LedgerPoints);

    public sealed record ArtistStat(
        ScoringEndpoints.ArtistRef Artist, decimal RawScore, decimal Percentile, int Rank, int Fans, bool IsEstimated,
        DateTime ComputedAt);

    public sealed record ListeningStats(
        int Artists, int LikedTracks, int PlaylistTracks, int DistinctAlbums, int RecentPlays, int SavedAlbums, int TopTracks,
        int FollowedArtists, int PlaysLastQuarter);

    public sealed record JournalStats(int Logs, int AlbumsLogged, int Relistens, int Reviews, int Ratings, int Lists, int Comments);

    public sealed record ProofStats(int Tickets, int Merch, int Screenshots);

    public sealed record UserStats(
        IReadOnlyList<ArtistStat> Artists, ListeningStats Listening, JournalStats Journal, ProofStats Proof, decimal LedgerPoints);

    private static async Task<IResult> GetMineAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, user.UserId(), own: true));
    }

    // The fan themself and their followers; anyone else gets 403 followers_only. Stats are followers-only, none public
    // by default (decided 2026-10-25).
    private static async Task<IResult> GetForUserAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var owner = await Follows.FollowEndpoints.LiveUserAsync(connection, handle);
        if (owner != me && !await Follows.FollowEndpoints.IsFollowingAsync(connection, me, owner))
        {
            throw new Infrastructure.ApiProblemException(StatusCodes.Status403Forbidden, "followers_only", "Followers only",
                "Fandom stats are shared with followers. Follow this fan to see theirs.");
        }

        var stats = await LoadAsync(connection, owner, own: owner == me);
        return Results.Ok(new ProfileStats(stats.Artists, stats.Journal, stats.Proof, stats.LedgerPoints));
    }

    /// <summary>One fan's numbers, read from stored state and the ledger.</summary>
    /// <summary>The fan's stats. Unless <paramref name="own"/>, journal counts leave out private and taken-down logs and private lists.</summary>
    public static async Task<UserStats> LoadAsync(NpgsqlConnection connection, Guid userId, bool own = true)
    {
        using var results = await connection.QueryMultipleAsync(
            """
            -- Per artist: my score, and my rank among the artist's scored fans (ties share the better rank).
            SELECT a.id AS Id, a.name AS Name, a.image_url AS ImageUrl, s.raw_score AS RawScore, s.percentile AS Percentile,
                   (SELECT count(*) FROM user_artist_scores o WHERE o.artist_id = s.artist_id AND o.raw_score > s.raw_score)::int + 1 AS Rank,
                   (SELECT count(*) FROM user_artist_scores o WHERE o.artist_id = s.artist_id)::int AS Fans,
                   s.is_estimated AS IsEstimated, s.computed_at AS ComputedAt
            FROM user_artist_scores s JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
            WHERE s.user_id = @userId
            ORDER BY s.percentile DESC, lower(a.name), a.id;

            SELECT count(*)::int AS Artists, COALESCE(sum(liked_tracks), 0)::int AS LikedTracks,
                   COALESCE(sum(playlist_tracks), 0)::int AS PlaylistTracks,
                   COALESCE(sum(distinct_albums), 0)::int AS DistinctAlbums, COALESCE(sum(recent_plays), 0)::int AS RecentPlays,
                   (SELECT count(*) FROM user_album_saves v JOIN albums al ON al.id = v.album_id AND al.delisted_at IS NULL
                     JOIN artists aa ON aa.id = al.artist_id AND aa.status = 'active' WHERE v.user_id = @userId)::int AS SavedAlbums,
                   COALESCE(sum(top_tracks), 0)::int AS TopTracks,
                   count(*) FILTER (WHERE followed)::int AS FollowedArtists,
                   (SELECT count(*) FROM user_plays p JOIN artists pa ON pa.id = p.artist_id AND pa.status = 'active'
                     WHERE p.user_id = @userId AND p.played_at >= now() - interval '90 days')::int AS PlaysLastQuarter
            FROM user_artist_signals s JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
            WHERE s.user_id = @userId;

            SELECT count(*)::int AS Logs, count(DISTINCT album_id)::int AS AlbumsLogged,
                   count(*) FILTER (WHERE is_relisten)::int AS Relistens,
                   count(*) FILTER (WHERE btrim(COALESCE(review, '')) <> '')::int AS Reviews,
                   count(*) FILTER (WHERE rating IS NOT NULL)::int AS Ratings,
                   (SELECT count(*) FROM lists WHERE user_id = @userId AND (@own OR visibility <> 'private'))::int AS Lists,
                   (SELECT count(*) FROM comments WHERE user_id = @userId AND deleted_at IS NULL)::int AS Comments
            FROM album_logs WHERE user_id = @userId AND deleted_at IS NULL
              AND (@own OR (visibility <> 'private' AND removed_at IS NULL));

            SELECT count(*) FILTER (WHERE kind = 'ticket')::int AS Tickets,
                   count(*) FILTER (WHERE kind = 'merch')::int AS Merch,
                   count(*) FILTER (WHERE kind = 'screenshot')::int AS Screenshots
            FROM submissions WHERE user_id = @userId AND status = 'verified';

            SELECT COALESCE(sum(points), 0) FROM score_events WHERE user_id = @userId AND reversed_at IS NULL;
            """,
            new { userId, own });

        var artists = (await results.ReadAsync<ArtistRow>()).Select(r => new ArtistStat(
            new ScoringEndpoints.ArtistRef(r.Id, r.Name, r.ImageUrl), r.RawScore, r.Percentile, r.Rank, r.Fans, r.IsEstimated,
            r.ComputedAt)).ToList();
        var listening = await results.ReadSingleAsync<ListeningStats>();
        var journal = await results.ReadSingleAsync<JournalStats>();
        var proof = await results.ReadSingleAsync<ProofStats>();
        var points = await results.ReadSingleAsync<decimal>();
        return new UserStats(artists, listening, journal, proof, points);
    }

    private sealed record ArtistRow(
        Guid Id, string Name, string? ImageUrl, decimal RawScore, decimal Percentile, int Rank, int Fans, bool IsEstimated,
        DateTime ComputedAt);
}
