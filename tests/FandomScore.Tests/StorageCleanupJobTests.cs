using Dapper;
using FandomScore.Database;
using FandomScore.Storage;
using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.StorageCleanup;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// The hourly storage cleanup (migration 0027) against a real database and a temporary directory as storage: no file
/// outlives its rows, and no file a live row names is deleted.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorageCleanupJobTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"storage-cleanup-tests-{Guid.NewGuid():N}");
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private int _runs;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"storage_cleanup_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public void Runs_hourly_as_storage_cleanup()
    {
        var job = Job();
        Assert.Equal("storage.cleanup", job.Kind);
        Assert.Equal(TimeSpan.FromHours(1), job.Every);
    }

    [Fact]
    public async Task Expired_unused_slots_go_and_everything_still_in_use_stays()
    {
        var fan = await UserAsync("fan");
        var staff = await UserAsync("staffer");
        var expired = await SlotAsync(fan, expiredFor: TimeSpan.FromDays(2));
        var neverFilled = await SlotAsync(fan, expiredFor: TimeSpan.FromDays(2), uploaded: false);
        var inGrace = await SlotAsync(fan, expiredFor: TimeSpan.FromHours(2));
        var open = await SlotAsync(fan, expiredFor: TimeSpan.FromMinutes(-30));
        var featured = await SlotAsync(staff, expiredFor: TimeSpan.FromDays(30), purpose: "featured_video");
        await FeatureAsync(featured.Id, staff);
        var picture = await SlotAsync(fan, expiredFor: TimeSpan.FromDays(30), purpose: "avatar");
        await PictureAsync(picture.Id, fan);
        var pending = await SubmissionAsync(fan, "needs_review", slotExpiredFor: TimeSpan.FromDays(10));
        var verified = await SubmissionAsync(fan, "verified", slotExpiredFor: TimeSpan.FromDays(10));
        var rejected = await SubmissionAsync(fan, "rejected", slotExpiredFor: TimeSpan.FromDays(10));

        await RunAsync();

        Assert.False(await UploadExistsAsync(expired.Id));
        Assert.False(FileExists(expired.Key));
        Assert.False(await UploadExistsAsync(neverFilled.Id));
        foreach (var kept in new[] { inGrace, open, featured, picture, pending, verified, rejected })
        {
            Assert.True(await UploadExistsAsync(kept.Id), $"upload {kept.Key} is still in use");
            Assert.True(FileExists(kept.Key), $"file {kept.Key} is still in use");
        }

        // Submission files stay, a just-rejected one included (it keeps its files for 30 days).
        Assert.Equal(3, await CountAsync("SELECT count(*) FROM submission_files"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM storage_deletions"));

        // Running again changes nothing.
        var files = StoredFiles();
        await RunAsync();
        Assert.Equal(files, StoredFiles());
        Assert.Equal(7, await CountAsync("SELECT count(*) FROM uploads"));
        await AssertEveryFileHasARowAsync();
    }

    [Fact]
    public async Task A_deleted_account_loses_every_file_after_30_days_and_then_purges()
    {
        var gone = await UserAsync("gone");
        var verified = await SubmissionAsync(gone, "verified", slotExpiredFor: TimeSpan.FromDays(40));
        var pending = await SubmissionAsync(gone, "needs_review", slotExpiredFor: TimeSpan.FromDays(40));
        var leftover = await SlotAsync(gone, expiredFor: TimeSpan.FromMinutes(-30));
        var recent = await UserAsync("recent");
        var recentFile = await SubmissionAsync(recent, "needs_review", slotExpiredFor: TimeSpan.FromDays(10));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE users SET deleted_at = now() - interval '31 days' WHERE id = @gone;
                UPDATE users SET deleted_at = now() - interval '5 days' WHERE id = @recent;
                """,
                new { gone, recent });
        }

        // The purge refuses while stored files remain.
        var purge = new PurgeDeletedAccountsJob(_db, NullLogger<PurgeDeletedAccountsJob>.Instance);
        await purge.RunAsync(new ClaimedJob(100, purge.Kind, "{}", 1, 10), CancellationToken.None);
        Assert.False(await PurgedAsync(gone));

        await RunAsync();
        foreach (var file in new[] { verified, pending, leftover })
        {
            Assert.False(FileExists(file.Key), $"{file.Key} of the deleted account is gone");
            Assert.False(await UploadExistsAsync(file.Id));
        }

        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM submission_files f JOIN submissions s ON s.id = f.submission_id WHERE s.user_id = @gone", new { gone }));

        // Within the 30 days nothing goes.
        Assert.True(FileExists(recentFile.Key));
        Assert.True(await UploadExistsAsync(recentFile.Id));

        await purge.RunAsync(new ClaimedJob(101, purge.Kind, "{}", 1, 10), CancellationToken.None);
        Assert.True(await PurgedAsync(gone));
        Assert.False(await PurgedAsync(recent));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM submissions WHERE user_id = @gone", new { gone }));

        await RunAsync();
        Assert.Equal(new[] { recentFile.Key }, StoredFiles());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM storage_deletions"));
        await AssertEveryFileHasARowAsync();
    }

    [Fact]
    public async Task Rejected_and_taken_down_photos_lose_their_files_after_30_days()
    {
        var fan = await UserAsync("fan");
        var oldRejected = await SubmissionAsync(fan, "rejected", slotExpiredFor: TimeSpan.FromDays(40));
        var newRejected = await SubmissionAsync(fan, "rejected", slotExpiredFor: TimeSpan.FromDays(40));
        var oldTakenDown = await SubmissionAsync(fan, "verified", slotExpiredFor: TimeSpan.FromDays(40));
        var newTakenDown = await SubmissionAsync(fan, "verified", slotExpiredFor: TimeSpan.FromDays(40));
        var reReviewed = await SubmissionAsync(fan, "rejected", slotExpiredFor: TimeSpan.FromDays(40));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE submissions SET rejected_at = now() - interval '31 days' WHERE id IN (@oldRejected, @reReviewed);
                UPDATE submissions SET rejected_at = now() - interval '29 days' WHERE id = @newRejected;
                UPDATE submissions SET removed_at = now() - interval '31 days' WHERE id = @oldTakenDown;
                UPDATE submissions SET removed_at = now() - interval '29 days' WHERE id = @newTakenDown;
                UPDATE submissions SET status = 'needs_review' WHERE id = @reReviewed;
                """,
                new
                {
                    oldRejected = oldRejected.SubmissionId, newRejected = newRejected.SubmissionId,
                    oldTakenDown = oldTakenDown.SubmissionId, newTakenDown = newTakenDown.SubmissionId,
                    reReviewed = reReviewed.SubmissionId,
                });
        }

        await RunAsync();
        foreach (var gone in new[] { oldRejected, oldTakenDown })
        {
            Assert.False(FileExists(gone.Key), $"{gone.Key} is past its 30 days");
            Assert.False(await UploadExistsAsync(gone.Id));
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM submission_files WHERE submission_id = @id", new { id = gone.SubmissionId }));
            Assert.Equal(1, await CountAsync("SELECT count(*) FROM submissions WHERE id = @id", new { id = gone.SubmissionId }));
        }

        foreach (var kept in new[] { newRejected, newTakenDown, reReviewed })
        {
            Assert.True(FileExists(kept.Key), $"{kept.Key} is kept");
            Assert.True(await UploadExistsAsync(kept.Id));
        }

        // The taken-down photo keeps its verified status (and so its points).
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM submissions WHERE id = @id AND status = 'verified'", new { id = oldTakenDown.SubmissionId }));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM storage_deletions"));
        await AssertEveryFileHasARowAsync();
    }

    [Fact]
    public async Task Files_whose_rows_were_deleted_elsewhere_are_removed()
    {
        var fan = await UserAsync("fan");
        var withdrawn = await SubmissionAsync(fan, "needs_review", slotExpiredFor: TimeSpan.Zero);
        var kept = await SubmissionAsync(fan, "needs_review", slotExpiredFor: TimeSpan.Zero);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // As if the API crashed after deleting the row and before deleting the file: the database queued the key.
            await connection.ExecuteAsync("DELETE FROM submissions WHERE id = @id", new { id = withdrawn.SubmissionId });
            // A queued key whose file is already gone is not an error.
            await connection.ExecuteAsync("INSERT INTO storage_deletions (storage_key) VALUES ('quarantine/already-gone')");
        }

        Assert.True(FileExists(withdrawn.Key));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM storage_deletions"));

        await RunAsync();
        Assert.False(FileExists(withdrawn.Key));
        Assert.True(FileExists(kept.Key));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM storage_deletions"));

        await RunAsync();
        Assert.Equal(new[] { kept.Key }, StoredFiles());
        await AssertEveryFileHasARowAsync();
    }

    [Fact]
    public async Task A_queued_key_that_a_live_row_still_names_is_not_deleted()
    {
        var fan = await UserAsync("fan");
        var live = await SubmissionAsync(fan, "verified", slotExpiredFor: TimeSpan.FromDays(3));
        var slot = await SlotAsync(fan, expiredFor: TimeSpan.FromMinutes(-30));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO storage_deletions (storage_key) VALUES (@a), (@b)", new { a = live.Key, b = slot.Key });
        }

        await RunAsync();
        Assert.True(FileExists(live.Key));
        Assert.True(FileExists(slot.Key));
        Assert.True(await UploadExistsAsync(live.Id));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM storage_deletions"));

        // Once its row goes, the key is queued again and the file goes with the next run.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("DELETE FROM uploads WHERE id = @id", new { id = slot.Id });
        }

        await RunAsync();
        Assert.False(FileExists(slot.Key));
        Assert.True(FileExists(live.Key));
    }

    private sealed record Stored(Guid Id, string Key, Guid? SubmissionId = null);

    private StorageCleanupJob Job() => new(_db, new LocalFileStore(_storage), NullLogger<StorageCleanupJob>.Instance);

    private async Task RunAsync()
    {
        var job = Job();
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, "{}", 1, 10), CancellationToken.None);
    }

    private async Task<Guid> UserAsync(string handle)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (handle, email) VALUES (@handle, @handle || '@example.test') RETURNING id", new { handle });
    }

    /// <summary>An upload slot that expired <paramref name="expiredFor"/> ago (negative: still open), with its file.</summary>
    private async Task<Stored> SlotAsync(Guid userId, TimeSpan expiredFor, bool uploaded = true, string purpose = "submission")
    {
        await using var connection = await _db.OpenConnectionAsync();
        var id = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, created_at, expires_at)
            VALUES (@userId, @purpose, @mime, 4, now() - @expiredFor - interval '1 hour', now() - @expiredFor) RETURNING id
            """,
            new { userId, purpose, mime = purpose == "featured_video" ? "video/mp4" : "image/jpeg", expiredFor });
        var key = $"quarantine/{id}";
        if (uploaded)
        {
            await new LocalFileStore(_storage).PutAsync(key, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
            await connection.ExecuteAsync(
                "UPDATE uploads SET storage_key = @key, sha256 = sha256(@key::bytea), bytes = 4, uploaded_at = expires_at - interval '30 minutes' WHERE id = @id",
                new { id, key });
        }

        return new Stored(id, key);
    }

    /// <summary>A submission with one stored photo, attached the way POST /submissions attaches it.</summary>
    private async Task<Stored> SubmissionAsync(Guid userId, string status, TimeSpan slotExpiredFor)
    {
        var slot = await SlotAsync(userId, slotExpiredFor);
        await using var connection = await _db.OpenConnectionAsync();
        var submissionId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO submissions (user_id, artist_id, kind, status, verified_at, rejection_reason)
            VALUES (@userId, @_artistId, 'merch', @status::submission_status,
                    CASE WHEN @status = 'verified' THEN now() END, CASE WHEN @status = 'rejected' THEN 'Not merch' END)
            RETURNING id
            """,
            new { userId, _artistId, status });
        await connection.ExecuteAsync(
            """
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
            SELECT @submissionId, storage_key, sha256, mime_type, bytes FROM uploads WHERE id = @id;
            UPDATE uploads SET submission_id = @submissionId WHERE id = @id;
            """,
            new { submissionId, id = slot.Id });
        return slot with { SubmissionId = submissionId };
    }

    /// <summary>Makes the upload the fan's profile picture (migration 0052), as PATCH /me does.</summary>
    private async Task PictureAsync(Guid uploadId, Guid fan)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE users SET avatar_upload_id = @uploadId, avatar_url = 'https://api.test/v1/avatars/' || @uploadId WHERE id = @fan",
            new { uploadId, fan });
    }

    private async Task FeatureAsync(Guid uploadId, Guid staff)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO featured_artists (week_start, artist_id, video_kind, video_upload_id, spotify_track_id, track_title, created_by)
            VALUES (date_trunc('week', now())::date, @_artistId, 'upload', @uploadId, '1aaaaaaaaaaaaaaaaaaaaa', 'Cowboy Nudes', @staff)
            """,
            new { _artistId, uploadId, staff });
    }

    private async Task<bool> UploadExistsAsync(Guid id)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM uploads WHERE id = @id)", new { id });
    }

    private async Task<bool> PurgedAsync(Guid id)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<bool>("SELECT content_purged_at IS NOT NULL FROM users WHERE id = @id", new { id });
    }

    private async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, args);
    }

    private bool FileExists(string key) => File.Exists(Path.Combine(_storage, key));

    private string[] StoredFiles() =>
        Directory.Exists(_storage)
            ? [.. Directory.EnumerateFiles(_storage, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_storage, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Every file on disk is named by an upload or submission-file row.</summary>
    private async Task AssertEveryFileHasARowAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        var named = (await connection.QueryAsync<string>(
            "SELECT storage_key FROM uploads WHERE storage_key IS NOT NULL UNION SELECT storage_key FROM submission_files")).ToHashSet();
        Assert.All(StoredFiles(), file => Assert.Contains(file, named));
    }
}
