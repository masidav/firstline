using System.Text.Json;
using Dapper;
using FandomScore.Database;
using FandomScore.Worker.ArtistOnboarding;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The percentile math, and the scoring job against a real database (migration 0008).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ScoringJobTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"scoring_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 4);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public void Equal_scores_get_equal_percentiles_and_higher_scores_rank_higher()
    {
        var result = Percentiles.Blend([10, 20, 20, 30], _ => 0, blendK: 1);

        Assert.Equal(result[1].Value, result[2].Value);
        Assert.True(result[0].Value < result[1].Value && result[2].Value < result[3].Value);
        // w = 4 / 5: real mid-ranks 1/8, 1/2, 1/2, 7/8; synthetic 0.
        Assert.Equal(10, result[0].Value);
        Assert.Equal(70, result[3].Value);
        Assert.Equal(0.2, result[0].BaselineWeight);
        Assert.False(result[0].IsEstimated);
    }

    [Fact]
    public void Few_fans_lean_on_the_baseline_and_are_estimates()
    {
        var result = Assert.Single(Percentiles.Blend([15], Percentiles.Lognormal(Math.Log(15), 1), blendK: 200));

        Assert.Equal(50, result.Value);
        Assert.Equal(0.995, result.BaselineWeight);
        Assert.True(result.IsEstimated);
    }

    [Fact]
    public void A_stored_curve_is_interpolated_and_flat_beyond_its_ends()
    {
        var cdf = Percentiles.Curve("[[10, 0.2], [20, 0.6], [40, 0.9]]");

        Assert.Equal(0, cdf(5));
        Assert.Equal(0.4, cdf(15), 9);
        Assert.Equal(0.75, cdf(30), 9);
        Assert.Equal(1, cdf(50));
    }

    [Fact]
    public async Task Scores_every_fan_with_an_even_split_and_ranks_them()
    {
        var top = await ScoringSetup.FanAsync(_db, "top_fan", _artistId, liked: 100, playlist: 100, albums: 4, rank: 1, recent: 50);
        var casual = await ScoringSetup.FanAsync(_db, "casual", _artistId, liked: 10, playlist: 0, albums: 1, rank: null, recent: 0);
        var once = await ScoringSetup.FanAsync(_db, "once", _artistId, liked: 0, playlist: 0, albums: 0, rank: null, recent: 1);

        await RescoreAsync();

        var scores = await ScoresAsync();
        var rules = await RuleCountAsync();   // 18 since migration 0015; the five classic signals are full for the top fan
        Assert.Equal(3, scores.Count);
        Assert.Equal(Math.Round(100.0 * 5 / rules, 2), (double)scores[top].RawScore, 2);
        // (ln 11 / ln 101 + 1/4) / n signals.
        Assert.Equal(Math.Round(100 * (Math.Log(11) / Math.Log(101) + 0.25) / rules, 2), (double)scores[casual].RawScore, 2);
        Assert.True(scores[top].Percentile > scores[casual].Percentile && scores[casual].Percentile > scores[once].Percentile);
        Assert.All(scores.Values, s => Assert.True(s.IsEstimated));
        Assert.All(scores.Values, s => Assert.Equal(0.985m, s.BaselineWeight));

        using var breakdown = JsonDocument.Parse(scores[top].Breakdown);
        var spotify = breakdown.RootElement.GetProperty("spotify");
        Assert.Equal(100, spotify.GetProperty("liked_tracks").GetInt32());
        Assert.Equal(4, spotify.GetProperty("catalog_releases").GetInt32());
        Assert.Equal(1m, spotify.GetProperty("shares").GetProperty("album_depth").GetDecimal());
        Assert.Equal(0m, spotify.GetProperty("shares").GetProperty("follow").GetDecimal());
        Assert.False(spotify.GetProperty("followed").GetBoolean());
        Assert.Equal(scores[top].RawScore, breakdown.RootElement.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_fan_without_signals_any_more_loses_their_score_and_reruns_are_idempotent()
    {
        var stays = await ScoringSetup.FanAsync(_db, "stays", _artistId, liked: 5, playlist: 0, albums: 1, rank: null, recent: 0);
        var leaves = await ScoringSetup.FanAsync(_db, "leaves", _artistId, liked: 50, playlist: 0, albums: 2, rank: 3, recent: 0);
        await RescoreAsync();
        var before = (await ScoresAsync())[stays];

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("DELETE FROM user_artist_signals WHERE user_id = @leaves", new { leaves });
        }

        await RescoreAsync();
        await RescoreAsync();

        var after = await ScoresAsync();
        Assert.Equal([stays], after.Keys);
        Assert.Equal(before.RawScore, after[stays].RawScore);
    }

    [Fact]
    public async Task A_weight_change_queues_a_rescore_that_applies_the_new_weights()
    {
        var fan = await ScoringSetup.FanAsync(_db, "fan", _artistId, liked: 100, playlist: 0, albums: 0, rank: null, recent: 0);
        await RescoreAsync();
        var rules = await RuleCountAsync();
        Assert.Equal(Math.Round(100.0 / rules, 2), (double)(await ScoresAsync())[fan].RawScore, 2);

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE scoring_rules SET points = 4 WHERE rule_key = 'spotify_liked_track'");
        }

        Assert.True(await RunQueuedAsync());
        Assert.Equal(Math.Round(100.0 * 4 / (rules + 3), 2), (double)(await ScoresAsync())[fan].RawScore, 2);
    }

    [Fact]
    public async Task A_paused_artist_has_its_scores_removed()
    {
        await ScoringSetup.FanAsync(_db, "fan", _artistId, liked: 3, playlist: 0, albums: 1, rank: null, recent: 0);
        await RescoreAsync();
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE artists SET status = 'paused' WHERE id = @_artistId", new { _artistId });
        }

        await RescoreAsync();

        Assert.Empty(await ScoresAsync());
    }

    private async Task RescoreAsync()
    {
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("SELECT request_artist_rescore(@_artistId)", new { _artistId });
        }

        Assert.True(await RunQueuedAsync());
    }

    private async Task<bool> RunQueuedAsync() => await ScoringSetup.RunQueuedAsync(_db);

    private async Task<int> RuleCountAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\\_%'");
    }

    private sealed record ScoreRow(Guid UserId, decimal RawScore, decimal Percentile, bool IsEstimated, decimal BaselineWeight, string Breakdown);

    private async Task<Dictionary<Guid, ScoreRow>> ScoresAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.QueryAsync<ScoreRow>(
                """
                SELECT user_id AS UserId, raw_score AS RawScore, percentile AS Percentile, is_estimated AS IsEstimated,
                       baseline_weight AS BaselineWeight, breakdown::text AS Breakdown
                FROM user_artist_scores WHERE artist_id = @_artistId
                """,
                new { _artistId }))
            .ToDictionary(r => r.UserId);
    }
}

/// <summary>Shared setup for the scoring tests: an active artist with a provisional baseline, and fans with signals.</summary>
internal static class ScoringSetup
{
    public static async Task<Guid> ActiveArtistAsync(NpgsqlDataSource db, string spotifyArtistId, string name, int releases)
    {
        await using var connection = await db.OpenConnectionAsync();
        var artistId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO artists (name, spotify_artist_id) VALUES (@name, @spotifyArtistId) RETURNING id", new { name, spotifyArtistId });
        await connection.ExecuteAsync(
            """
            INSERT INTO albums (artist_id, title, spotify_album_id)
              SELECT @artistId, 'Album ' || n, @spotifyArtistId || '-al-' || n FROM generate_series(1, @releases) n;
            INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES (@artistId, '{"provisional": true}', '[[1, 0.5]]');
            UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE id = @artistId;
            """,
            new { artistId, spotifyArtistId, releases });
        return artistId;
    }

    public static async Task<Guid> FanAsync(
        NpgsqlDataSource db, string handle, Guid artistId, int liked, int playlist, int albums, short? rank, int recent)
    {
        await using var connection = await db.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid?>("SELECT id FROM users WHERE handle = @handle", new { handle })
                     ?? await connection.ExecuteScalarAsync<Guid>(
                         "INSERT INTO users (handle, email) VALUES (@handle, @handle || '@example.test') RETURNING id", new { handle });
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums, top_artist_rank, recent_plays)
            VALUES (@userId, @artistId, @liked, @playlist, @albums, @rank, @recent)
            """,
            new { userId, artistId, liked, playlist, albums, rank, recent });
        return userId;
    }

    /// <summary>Runs the next queued job through the runner, as the worker would.</summary>
    public static async Task<bool> RunQueuedAsync(NpgsqlDataSource db)
    {
        var jobOptions = Microsoft.Extensions.Options.Options.Create(new JobOptions());
        var runner = new JobRunner(new JobQueue(db, jobOptions),
            [new ScoreArtistJob(db, Microsoft.Extensions.Options.Options.Create(new BaselineOptions()), NullLogger<ScoreArtistJob>.Instance)],
            jobOptions, TimeProvider.System, NullLogger<JobRunner>.Instance);
        return await runner.RunOnceAsync(CancellationToken.None);
    }
}
