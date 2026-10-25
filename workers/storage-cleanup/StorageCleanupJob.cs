using Dapper;
using FandomScore.Storage;
using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.StorageCleanup;

/// <summary>
/// Hourly: removes stored files nothing needs any more (migration 0027). Rows go first, and the database writes each
/// removed key to <c>storage_deletions</c> in the same statement; then the file is deleted from storage, then that
/// row. A crash anywhere leaves a row naming the file, so the next run finishes the job. Removes:
/// <list type="bullet">
/// <item>upload slots that expired unused: not attached to a submission, not a featured artist's video, and
/// <see cref="SlotGrace"/> past their expiry;</item>
/// <item>every file of an account deleted more than 30 days ago and not purged yet (its submission files and upload
/// slots), so <see cref="PurgeDeletedAccountsJob"/> can then purge it;</item>
/// <item>files whose rows were deleted elsewhere (a withdrawn submission, a purge, any cascade) and are still
/// queued, for example because the API crashed or storage failed before it deleted them.</item>
/// </list>
/// A queued key that a live row still names is never deleted. Files of rejected submissions stay: their retention
/// is an open decision (PROJECT_TODO.md).
/// </summary>
public sealed partial class StorageCleanupJob(NpgsqlDataSource db, IFileStore files, ILogger<StorageCleanupJob> logger) : IJobHandler
{
    public const string JobKind = "storage.cleanup";

    /// <summary>How long after its expiry an unused slot is kept (a fan finishing a submission, staff a feature).</summary>
    public static readonly TimeSpan SlotGrace = TimeSpan.FromDays(1);

    private const int BatchSize = 200;

    /// <summary>Batches per step per run; whatever is left waits for the next run.</summary>
    private const int MaxBatches = 25;

    private const string NotFeatured = "NOT EXISTS (SELECT 1 FROM featured_artists f WHERE f.video_upload_id = uploads.id)";

    private const string DueAccounts =
        "SELECT id FROM users WHERE deleted_at <= now() - @RetentionWindow::interval AND content_purged_at IS NULL";

    public string Kind => JobKind;

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var args = new { BatchSize, PurgeDeletedAccountsJob.RetentionWindow, grace = SlotGrace };

        // Deleted accounts due for purging: every submission file, then every upload slot (attached ones name the
        // same keys). A featured video stays (staff content, still on the feed's history).
        var accountFiles = await DeleteInBatchesAsync(connection,
            $"""
            DELETE FROM submission_files WHERE id IN (
              SELECT f.id FROM submission_files f JOIN submissions s ON s.id = f.submission_id
              WHERE s.user_id IN ({DueAccounts}) LIMIT @BatchSize)
            """,
            args, cancellationToken);
        accountFiles += await DeleteInBatchesAsync(connection,
            $"""
            DELETE FROM uploads WHERE id IN (
              SELECT id FROM uploads WHERE user_id IN ({DueAccounts}) AND {NotFeatured} LIMIT @BatchSize)
              AND {NotFeatured}
            """,
            args, cancellationToken);

        // Slots that expired unused. The conditions are repeated on the delete itself, so a slot attached or featured
        // since the select is skipped (attaching locks the row, and the delete re-checks it).
        var expiredSlots = await DeleteInBatchesAsync(connection,
            $"""
            DELETE FROM uploads WHERE id IN (
              SELECT id FROM uploads
              WHERE submission_id IS NULL AND expires_at < now() - @grace AND {NotFeatured}
              ORDER BY expires_at LIMIT @BatchSize)
              AND submission_id IS NULL AND {NotFeatured}
            """,
            args, cancellationToken);

        var (deleted, kept) = await DeleteQueuedFilesAsync(connection, cancellationToken);
        LogCleaned(logger, expiredSlots, accountFiles, deleted, kept);
    }

    private static async Task<int> DeleteInBatchesAsync(
        NpgsqlConnection connection, string sql, object args, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await connection.ExecuteAsync(sql, args);
            total += rows;
            if (rows < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Deletes queued files from storage, then their queue rows. Keys a live row still names are only dequeued.</summary>
    private async Task<(int Deleted, int Kept)> DeleteQueuedFilesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string Live =
            """
            (EXISTS (SELECT 1 FROM uploads WHERE storage_key = @key)
             OR EXISTS (SELECT 1 FROM submission_files WHERE storage_key = @key))
            """;
        var deleted = 0;
        var kept = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var keys = (await connection.QueryAsync<string>(
                "SELECT storage_key FROM storage_deletions ORDER BY queued_at, storage_key LIMIT @BatchSize", new { BatchSize })).ToList();
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await connection.ExecuteScalarAsync<bool>($"SELECT {Live}", new { key }))
                {
                    // Still in use. If that row goes later, its delete queues the key again.
                    kept++;
                }
                else
                {
                    await files.DeleteAsync(key, cancellationToken);   // a missing file is fine
                    deleted++;
                }

                await connection.ExecuteAsync("DELETE FROM storage_deletions WHERE storage_key = @key", new { key });
            }

            if (keys.Count < BatchSize)
            {
                break;
            }
        }

        return (deleted, kept);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Storage cleanup: {ExpiredSlots} expired upload slots and {AccountFiles} deleted-account files removed; {Deleted} stored files deleted ({Kept} queued keys still in use).")]
    private static partial void LogCleaned(ILogger logger, int expiredSlots, int accountFiles, int deleted, int kept);
}
