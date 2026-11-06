using Dapper;
using FandomScore.Database;
using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// The retention switches for undecided periods (migration 0060, <c>Retention__*</c>): nothing is pruned until a period
/// is set, and each switch prunes only its own record.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionSwitchTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"retention_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await ExecuteAsync(
            """
            -- No staff account here: the needs_review submission below would write them a staff_review notice.
            INSERT INTO users (handle, email) VALUES ('ret_fan', 'ret_fan@example.test');
            WITH u AS (SELECT id FROM users WHERE handle = 'ret_fan')
            INSERT INTO notifications (user_id, kind, subject_id, data, created_at, read_at)
            SELECT id, 'presale', gen_random_uuid(), '{}', now() - age, read FROM u, (VALUES
              (interval '400 days', now() - interval '200 days'),    -- old and read long ago
              (interval '400 days', NULL),                           -- old, never read
              (interval '10 days', now() - interval '1 day'),        -- recent, read
              (interval '10 days', NULL)) v(age, read);              -- recent, unread
            INSERT INTO outbound_clicks (user_id, artist_id, kind, clicked_at)
            SELECT u.id, @_artistId, (SELECT min(k) FROM unnest(enum_range(NULL::outbound_kind)) k)::outbound_kind, now() - age
            FROM users u, (VALUES (interval '400 days'), (interval '10 days')) v(age) WHERE u.handle = 'ret_fan';
            INSERT INTO submissions (user_id, artist_id, kind, status, extracted, verified_at, rejected_at)
            SELECT u.id, @_artistId, 'ticket', s.status::submission_status, '{"text": "ORDER 123"}', s.verified, s.rejected
            FROM users u, (VALUES
              ('verified', now() - interval '40 days', NULL),         -- decided long ago
              ('verified', now() - interval '10 days', NULL),         -- decided recently
              ('rejected', NULL, now() - interval '40 days'),         -- rejected long ago
              ('needs_review', NULL, NULL)) s(status, verified, rejected)   -- not decided yet
            WHERE u.handle = 'ret_fan';
            -- Becoming rejected stamps rejected_at with now() (migration 0028); age it afterwards.
            UPDATE submissions SET rejected_at = now() - interval '40 days' WHERE status = 'rejected';
            """,
            new { _artistId });
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Unset_periods_keep_everything()
    {
        await RunAsync(new RetentionOptions());
        Assert.Equal(4, await ScalarAsync<int>("SELECT count(*) FROM notifications"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM outbound_clicks WHERE user_id IS NOT NULL"));
        Assert.Equal(4, await ScalarAsync<int>("SELECT count(*) FROM submissions WHERE extracted <> '{}'"));
    }

    [Fact]
    public async Task Read_notifications_go_after_their_period_unread_ones_stay()
    {
        await RunAsync(new RetentionOptions { ReadNotifications = TimeSpan.FromDays(180) });
        Assert.Equal(3, await ScalarAsync<int>("SELECT count(*) FROM notifications"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM notifications WHERE read_at < now() - interval '180 days'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM outbound_clicks WHERE user_id IS NOT NULL"));   // untouched
    }

    [Fact]
    public async Task All_notifications_go_after_their_period()
    {
        await RunAsync(new RetentionOptions { Notifications = TimeSpan.FromDays(365) });
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM notifications"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM notifications WHERE created_at < now() - interval '365 days'"));
    }

    [Fact]
    public async Task Old_clicks_forget_the_fan_but_stay_counted()
    {
        await RunAsync(new RetentionOptions { ClickLinks = TimeSpan.FromDays(365) });
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM outbound_clicks"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM outbound_clicks WHERE user_id IS NOT NULL"));
        Assert.Equal(4, await ScalarAsync<int>("SELECT count(*) FROM notifications"));   // untouched
    }

    [Fact]
    public async Task Ocr_text_is_cleared_once_a_submission_has_been_decided_for_the_period()
    {
        await RunAsync(new RetentionOptions { OcrText = TimeSpan.FromDays(30) });
        Assert.Equal(["needs_review", "verified"], await ListAsync<string>("SELECT status::text FROM submissions WHERE extracted <> '{}' ORDER BY status::text"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM submissions WHERE extracted = '{}'"));
        await RunAsync(new RetentionOptions { OcrText = TimeSpan.FromDays(30) });   // safe to repeat
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM submissions WHERE extracted = '{}'"));
    }

    private async Task RunAsync(RetentionOptions options)
    {
        var job = new PruneExpiredRecordsJob(_db, Microsoft.Extensions.Options.Options.Create(options), NullLogger<PruneExpiredRecordsJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
    }

    private async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, args);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<List<T>> ListAsync<T>(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<T>(sql)];
    }
}
