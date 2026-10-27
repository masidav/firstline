using Dapper;
using FandomScore.Database;
using FandomScore.Worker.Allocations;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Runs the worker's draw jobs inside API tests, standing in for the worker process.</summary>
public static class Draws
{
    /// <summary>Runs every queued <c>allocations.select</c> job once, as the worker would, and marks it finished.</summary>
    public static async Task RunQueuedAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        var jobs = (await connection.QueryAsync<(long Id, string Payload)>(
            "SELECT id, payload::text FROM jobs WHERE kind = @kind AND status = 'queued'", new { kind = SelectAllocationJob.JobKind })).ToList();
        var handler = new SelectAllocationJob(db, NullLogger<SelectAllocationJob>.Instance);
        foreach (var (id, payload) in jobs)
        {
            await handler.RunAsync(new ClaimedJob(id, SelectAllocationJob.JobKind, payload, 1, 10), CancellationToken.None);
            await connection.ExecuteAsync("UPDATE jobs SET status = 'succeeded', finished_at = now() WHERE id = @id", new { id });
        }
    }
}

/// <summary>The draw as a worker job (migration 0036): once per allocation, harmless on a cancelled one, refused while open.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SelectAllocationJobTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"select_job_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-draw", "Draw Band", releases: 1);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Runs_the_draw_once_and_a_second_run_changes_nothing()
    {
        var allocationId = await ClosedAllocationAsync(tickets: 2, fans: 3);
        var job = new SelectAllocationJob(_db, NullLogger<SelectAllocationJob>.Instance);
        var claimed = new ClaimedJob(1, job.Kind, $$"""{"allocation_id":"{{allocationId}}","ran_by":null}""", 1, 10);

        await job.RunAsync(claimed, CancellationToken.None);
        var first = await SeedAsync(allocationId);
        await job.RunAsync(claimed, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, await SeedAsync(allocationId));
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal((2, 3), await connection.QuerySingleAsync<(int, int)>(
            "SELECT selected_fans, registrations FROM allocation_selections WHERE allocation_id = @allocationId", new { allocationId }));
        Assert.Equal("allocations.select", job.Kind);
        Assert.Null(job.Every);
    }

    [Fact]
    public async Task A_cancelled_allocation_ends_the_job_without_a_draw()
    {
        var allocationId = await ClosedAllocationAsync(tickets: 2, fans: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE allocations SET cancelled_at = now() WHERE id = @allocationId", new { allocationId });
        }

        var job = new SelectAllocationJob(_db, NullLogger<SelectAllocationJob>.Instance);
        await job.RunAsync(new ClaimedJob(2, job.Kind, $$"""{"allocation_id":"{{allocationId}}","ran_by":null}""", 1, 10), CancellationToken.None);

        Assert.Null(await SeedAsync(allocationId));
    }

    [Fact]
    public async Task A_draw_while_registration_is_open_fails_the_attempt_so_it_is_retried()
    {
        var allocationId = await ClosedAllocationAsync(tickets: 2, fans: 1, closesInDays: 5);

        var job = new SelectAllocationJob(_db, NullLogger<SelectAllocationJob>.Instance);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            job.RunAsync(new ClaimedJob(3, job.Kind, $$"""{"allocation_id":"{{allocationId}}","ran_by":null}""", 1, 10), CancellationToken.None));

        Assert.Equal("selection blocked: registration_open", error.MessageText);
        Assert.Null(await SeedAsync(allocationId));
    }

    // A published Chicago allocation whose registration closed (or closes in closesInDays), with fans registered.
    private async Task<Guid> ClosedAllocationAsync(int tickets, int fans, double closesInDays = -0.01)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Draw show', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        var allocationId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, @tickets, 4500, 0, now() - interval '1 day', now() + interval '10 days', now()) RETURNING id
            """,
            new { eventId, tickets });
        for (var i = 0; i < fans; i++)
        {
            var userId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO users (handle, email) VALUES (@handle, @email) RETURNING id",
                new { handle = $"draw_{allocationId:N}"[..20] + $"_{i}", email = $"draw_{allocationId:N}_{i}@example.test" });
            await connection.ExecuteAsync("INSERT INTO user_hub_cities (user_id, position, city_id) VALUES (@userId, 1, 'us-il-chicago')", new { userId });
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                VALUES (@userId, @_artistId, @score, 50, false, 0.5)
                """,
                new { userId, _artistId, score = 40 + i });
            await connection.ExecuteAsync("SELECT register_for_allocation(@userId, @allocationId)", new { userId, allocationId });
        }

        await connection.ExecuteAsync(
            """
            ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
            UPDATE allocations SET registration_closes_at = now() + make_interval(secs => @closesInDays * 86400) WHERE id = @allocationId;
            ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
            """,
            new { allocationId, closesInDays });
        return allocationId;
    }

    private async Task<string?> SeedAsync(Guid allocationId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string?>("SELECT seed FROM allocation_selections WHERE allocation_id = @allocationId", new { allocationId });
    }
}
