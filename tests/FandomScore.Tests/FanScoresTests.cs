using Dapper;
using FandomScore.Database;
using FandomScore.Worker.Fans;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// The two overall scores (migration 0039): trust only breaks ties in the ticket draw; the fan level is 1 to 5 from
/// equally weighted activity.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FanScoresTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"fan_scores_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-tie", "Tie Band", releases: 3);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Trust_breaks_a_tie_in_every_draw_but_never_beats_a_higher_score()
    {
        var trusted = await FanAsync("trusted", ageDays: 400, verifiedProof: 3);
        var fresh = await FanAsync("fresh", ageDays: 1, verifiedProof: 0);

        // Equal scores: the trusted fan wins every draw, whatever the seed (6 draws: by chance alone, 1 in 64).
        for (var i = 0; i < 6; i++)
        {
            var allocationId = await DrawAsync((trusted, 50m), (fresh, 50m));
            Assert.Equal(["trusted"], await SelectedAsync(allocationId));
        }

        // A higher score still wins over more trust.
        var scoreWins = await DrawAsync((trusted, 50m), (fresh, 51m));
        Assert.Equal(["fresh"], await SelectedAsync(scoreWins));

        // The trust each fan had is kept with the draw, and can't be edited.
        await using var connection = await _db.OpenConnectionAsync();
        var snapshots = (await connection.QueryAsync<(string Handle, decimal Trust)>(
            """
            SELECT u.handle::text, t.trust FROM allocation_trust_snapshots t
            JOIN allocation_registrations r ON r.id = t.registration_id JOIN users u ON u.id = r.user_id
            WHERE t.allocation_id = @scoreWins ORDER BY t.trust DESC
            """,
            new { scoreWins })).ToList();
        Assert.Equal(["trusted", "fresh"], snapshots.Select(s => s.Handle));
        Assert.True(snapshots[0].Trust > snapshots[1].Trust);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            connection.ExecuteAsync("UPDATE allocation_trust_snapshots SET trust = 1 WHERE allocation_id = @scoreWins", new { scoreWins }));
        Assert.Contains("never edited", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Levels_rise_with_equally_weighted_activity_and_the_hourly_job_stores_them()
    {
        var idle = await FanAsync("idle", ageDays: 10, verifiedProof: 0, scored: false);
        var busy = await FanAsync("busy", ageDays: 10, verifiedProof: 5, percentile: 90);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO album_logs (user_id, album_id)
                SELECT @busy, al.id FROM albums al WHERE al.artist_id = @_artistId;
                """,
                new { busy, _artistId });
        }

        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT fan_level_for(parts_average(fan_activity_parts(@idle)))", new { idle }));
            // fandom 0.30 (90 of 300), albums ln(4)/ln(51) = 0.35, proof 1, trivia 0, prompts 0: average 0.33, level 4.
            var activity = await connection.ExecuteScalarAsync<decimal>("SELECT parts_average(fan_activity_parts(@busy))", new { busy });
            Assert.InRange(activity, 0.32m, 0.34m);
            // Ten levels (0040), a tenth of activity each.
            Assert.Equal(new short[] { 1, 1, 2, 6, 8, 9, 10, 10 }, (await connection.QueryAsync<short>(
                "SELECT fan_level_for(x) FROM unnest(ARRAY[0, 0.09, 0.1, 0.5, 0.79, 0.8, 0.9, 1]::numeric[]) x")).ToArray());
        }

        var job = new FanScoresJob(_db, NullLogger<FanScoresJob>.Instance);
        Assert.Equal(("fans.overall_scores", TimeSpan.FromHours(1)), (job.Kind, job.Every));
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(new[] { ("busy", (short)4), ("idle", (short)1) }, (await connection.QueryAsync<(string, short)>(
                "SELECT u.handle::text, f.level FROM fan_overall_scores f JOIN users u ON u.id = f.user_id ORDER BY u.handle")).ToArray());
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE id = @idle", new { idle });
        }

        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM fan_overall_scores WHERE user_id = @idle", new { idle }));
        }
    }

    private async Task<Guid> FanAsync(string handle, int ageDays, int verifiedProof, bool scored = true, decimal percentile = 50)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (handle, email, created_at) VALUES (@handle, @handle || '@example.test', now() - make_interval(days => @ageDays)) RETURNING id",
            new { handle, ageDays });
        await connection.ExecuteAsync("INSERT INTO user_hub_cities (user_id, position, city_id) VALUES (@userId, 1, 'us-il-chicago')", new { userId });
        if (scored)
        {
            await connection.ExecuteAsync(
                "INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight) VALUES (@userId, @_artistId, 50, @percentile, false, 0.5)",
                new { userId, _artistId, percentile });
        }

        for (var i = 0; i < verifiedProof; i++)
        {
            var submission = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO submissions (user_id, artist_id, kind) VALUES (@userId, @_artistId, 'merch') RETURNING id", new { userId, _artistId });
            await connection.ExecuteAsync("UPDATE submissions SET status = 'verified' WHERE id = @submission", new { submission });
        }

        return userId;
    }

    // A published Chicago allocation with one ticket, the given fans registered at the given snapshot scores, drawn.
    private async Task<Guid> DrawAsync(params (Guid UserId, decimal Score)[] fans)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Tie show', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        var allocationId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, 1, 4500, 0, now() - interval '1 day', now() + interval '10 days', now()) RETURNING id
            """,
            new { eventId });
        foreach (var (userId, score) in fans)
        {
            await connection.ExecuteAsync("UPDATE user_artist_scores SET raw_score = @score WHERE user_id = @userId AND artist_id = @_artistId",
                new { score, userId, _artistId });
            await connection.ExecuteAsync("SELECT register_for_allocation(@userId, @allocationId)", new { userId, allocationId });
        }

        await connection.ExecuteAsync(
            """
            ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
            UPDATE allocations SET registration_closes_at = now() - interval '1 minute' WHERE id = @allocationId;
            ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
            SELECT run_allocation_selection(@allocationId, NULL);
            """,
            new { allocationId });
        return allocationId;
    }

    private async Task<string[]> SelectedAsync(Guid allocationId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<string>(
            """
            SELECT u.handle::text FROM allocation_registrations r JOIN users u ON u.id = r.user_id
            WHERE r.allocation_id = @allocationId AND r.outcome = 'selected'
            """,
            new { allocationId })];
    }
}
