using System.Text.Json;
using Dapper;
using FandomScore.Worker.ArtistOnboarding;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.Scoring;

/// <summary>
/// Scores every fan of one artist (payload <c>{"artist_id": ...}</c>, queued by <c>request_artist_rescore()</c> after
/// a sync, a weight change, or the daily schedule). Raw scores come from <c>v_raw_scores</c> (weights in
/// <c>scoring_rules</c>); percentiles blend them with the artist's baseline. Rewrites the artist's rows in
/// <c>user_artist_scores</c>, so it is idempotent. Artists that are not active have their scores removed.
/// </summary>
public sealed partial class ScoreArtistJob(
    NpgsqlDataSource db, IOptions<BaselineOptions> baselineOptions, ILogger<ScoreArtistJob> logger) : IJobHandler
{
    public const string JobKind = "scoring.score_artist";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Kind => JobKind;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var payload = JsonDocument.Parse(job.Payload);
        var artistId = payload.RootElement.GetProperty("artist_id").GetGuid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // One scoring run per artist at a time; a second waits and then scores the newer data.
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('scoring:' || @artistId::text, 0))",
            new { artistId }, transaction);

        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT status::text FROM artists WHERE id = @artistId", new { artistId }, transaction);
        if (status != "active")
        {
            var removed = await connection.ExecuteAsync(
                "DELETE FROM user_artist_scores WHERE artist_id = @artistId", new { artistId }, transaction);
            await transaction.CommitAsync(cancellationToken);
            LogSkipped(logger, artistId, status ?? "missing", removed);
            return;
        }

        var baseline = await connection.QuerySingleOrDefaultAsync<BaselineRow>(
            "SELECT dist_params::text AS DistParams, curve::text AS Curve, blend_k AS BlendK FROM artist_baselines WHERE artist_id = @artistId",
            new { artistId }, transaction)
            ?? throw new InvalidOperationException($"Active artist {artistId} has no baseline.");
        var (cdf, blendK) = Synthetic(baseline, baselineOptions.Value);

        var rows = (await connection.QueryAsync<RawScoreRow>(
            """
            SELECT user_id AS UserId, liked_tracks AS LikedTracks, playlist_tracks AS PlaylistTracks,
                   distinct_albums AS DistinctAlbums, top_artist_rank AS TopArtistRank, recent_plays AS RecentPlays,
                   catalog_releases AS CatalogReleases, liked_share AS LikedShare, playlist_share AS PlaylistShare,
                   depth_share AS DepthShare, top_share AS TopShare, recent_share AS RecentShare,
                   spotify_score AS SpotifyScore, ledger_points AS LedgerPoints, raw_score AS RawScore
            FROM v_raw_scores WHERE artist_id = @artistId
            """,
            new { artistId }, transaction)).ToList();
        var percentiles = Percentiles.Blend([.. rows.Select(r => (double)r.RawScore)], cdf, blendK);

        await connection.ExecuteAsync(
            """
            DELETE FROM user_artist_scores WHERE artist_id = @artistId AND NOT (user_id = ANY(@userIds));
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight,
                                            breakdown, computed_at)
            SELECT u, @artistId, r, p, e, w, b::jsonb, now()
            FROM unnest(@userIds, @raw, @percentile, @estimated, @baselineWeight, @breakdown) AS x (u, r, p, e, w, b)
            ON CONFLICT (user_id, artist_id) DO UPDATE
            SET raw_score = EXCLUDED.raw_score, percentile = EXCLUDED.percentile, is_estimated = EXCLUDED.is_estimated,
                baseline_weight = EXCLUDED.baseline_weight, breakdown = EXCLUDED.breakdown, computed_at = EXCLUDED.computed_at;
            """,
            new
            {
                artistId,
                userIds = rows.Select(r => r.UserId).ToArray(),
                raw = rows.Select(r => r.RawScore).ToArray(),
                percentile = percentiles.Select(p => (decimal)p.Value).ToArray(),
                estimated = percentiles.Select(p => p.IsEstimated).ToArray(),
                baselineWeight = percentiles.Select(p => (decimal)p.BaselineWeight).ToArray(),
                breakdown = rows.Select(Breakdown).ToArray(),
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        LogScored(logger, artistId, rows.Count);
    }

    /// <summary>
    /// Provisional baselines follow the current <see cref="BaselineOptions"/>, so tuning them needs no data
    /// migration. A baseline set by hand (not provisional) uses its stored curve and blend_k.
    /// </summary>
    private static (Func<double, double> Cdf, int BlendK) Synthetic(BaselineRow baseline, BaselineOptions options)
    {
        using var distParams = JsonDocument.Parse(baseline.DistParams);
        var provisional = distParams.RootElement.TryGetProperty("provisional", out var flag) && flag.ValueKind == JsonValueKind.True;
        return provisional
            ? (Percentiles.Lognormal(Math.Log(options.MedianScore), options.Sigma), options.BlendK)
            : (Percentiles.Curve(baseline.Curve), baseline.BlendK);
    }

    private static string Breakdown(RawScoreRow row) => JsonSerializer.Serialize(new
    {
        Spotify = new
        {
            row.LikedTracks,
            row.PlaylistTracks,
            row.DistinctAlbums,
            row.TopArtistRank,
            row.RecentPlays,
            row.CatalogReleases,
            Shares = new
            {
                Liked = row.LikedShare,
                Playlist = row.PlaylistShare,
                AlbumDepth = row.DepthShare,
                TopArtist = row.TopShare,
                Recent = row.RecentShare,
            },
            Points = row.SpotifyScore,
        },
        row.LedgerPoints,
        Total = row.RawScore,
    }, Json);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scored {Fans} fans of artist {ArtistId}")]
    private static partial void LogScored(ILogger logger, Guid artistId, int fans);

    [LoggerMessage(Level = LogLevel.Information, Message = "Artist {ArtistId} is {Status}; removed {Removed} scores")]
    private static partial void LogSkipped(ILogger logger, Guid artistId, string status, int removed);

    private sealed record BaselineRow(string DistParams, string Curve, int BlendK);

    private sealed record RawScoreRow(
        Guid UserId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, short? TopArtistRank, int RecentPlays,
        long CatalogReleases, decimal? LikedShare, decimal? PlaylistShare, decimal? DepthShare, decimal? TopShare,
        decimal? RecentShare, decimal SpotifyScore, decimal LedgerPoints, decimal RawScore);
}

/// <summary>Daily safety net: queues a rescore of every active artist (a missed or failed rescore heals within a day).</summary>
public sealed class RescoreAllJob(NpgsqlDataSource db) : IJobHandler
{
    public string Kind => "scoring.rescore_all";

    public TimeSpan? Every => TimeSpan.FromDays(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("SELECT request_full_rescore()");
    }
}
