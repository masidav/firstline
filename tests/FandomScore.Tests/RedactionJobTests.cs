using Dapper;
using FandomScore.Database;
using FandomScore.Storage;
using FandomScore.Worker.ImageRedaction;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;

namespace FandomScore.Tests;

/// <summary>
/// Redaction and archive (migration 0029, workers/image-redaction) against a real database and a temporary directory
/// as storage, with synthetic tickets: codes are found and blacked out before anything reaches the archive, and what
/// the scanner can't settle goes to staff with the original still in quarantine.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RedactionJobTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"redaction-tests-{Guid.NewGuid():N}");
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _fan;
    private int _runs;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"redaction_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await using var connection = await _db.OpenConnectionAsync();
        _fan = await connection.ExecuteScalarAsync<Guid>("INSERT INTO users (handle, email) VALUES ('fan', 'fan@example.test') RETURNING id");
        await connection.ExecuteAsync("INSERT INTO users (handle, email, is_staff) VALUES ('staffer', 'staffer@example.test', true)");
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
    public void The_scanner_finds_and_blacks_out_every_kind_of_code_even_tilted_or_off_centre()
    {
        var ticket = Ticket(qr: true, barcode: true, aztec: true, dataMatrix: true, pdf417: true, tilt: 17);
        Assert.True(Decode(ticket).Count >= 2, "the fixture's codes are readable before redacting");

        var result = CodeRedactor.Redact(ticket, "image/jpeg");

        Assert.False(result.Unreadable);
        Assert.True(result.CodesRedacted >= 5, $"found {result.CodesRedacted} of 5 codes");
        Assert.Equal(0, result.CodesRemaining);
        Assert.Empty(Decode(result.Content!));
        using var redacted = SKBitmap.Decode(result.Content);
        Assert.Equal((1400, 1000), (redacted.Width, redacted.Height));
    }

    [Fact]
    public void Unreadable_bytes_are_reported_not_thrown()
    {
        var result = CodeRedactor.Redact([1, 2, 3, 4, 5], "image/png");
        Assert.True(result.Unreadable);
    }

    [Fact]
    public async Task A_verified_ticket_is_redacted_and_moved_to_the_archive()
    {
        var (submission, file, key) = await SubmissionAsync("ticket", Ticket(qr: true, barcode: true), status: "needs_review");
        await VerifyAsync(submission);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM jobs WHERE kind = 'submissions.redact' AND dedupe_key = 'redact:' || @submission",
            new { submission }));

        await RunAsync(submission);

        var row = await FileAsync(file);
        Assert.Equal(("archived", "archive", true), (row.State, row.Tier, row.Redacted));
        Assert.Equal($"archive/{file:N}.jpg", row.StorageKey);
        Assert.True(row.Codes >= 2);
        var archived = await File.ReadAllBytesAsync(Path.Combine(_storage, row.StorageKey));
        Assert.Empty(Decode(archived));

        // The upload slot follows the file, the quarantined original is queued for deletion, and the submission counts
        // as archived.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM uploads WHERE storage_key = @k", new { k = row.StorageKey }));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM storage_deletions WHERE storage_key = @key", new { key }));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM submissions WHERE id = @submission AND archived_at IS NOT NULL", new { submission }));

        // Running again changes nothing.
        await RunAsync(submission);
        Assert.Equal(row, await FileAsync(file));
    }

    [Fact]
    public async Task Merch_with_no_code_is_archived_but_a_ticket_with_no_code_goes_to_staff_until_approved()
    {
        var (merch, merchFile, _) = await SubmissionAsync("merch", Ticket());
        var (ticket, ticketFile, ticketKey) = await SubmissionAsync("ticket", Ticket());

        await RunAsync(merch);
        await RunAsync(ticket);

        Assert.Equal("archived", (await FileAsync(merchFile)).State);
        var waiting = await FileAsync(ticketFile);
        Assert.Equal(("needs_review", ticketKey), (waiting.State, waiting.StorageKey));
        Assert.Equal("No barcode or QR code was found on this ticket. Check it by eye.", waiting.Note);
        Assert.True(File.Exists(Path.Combine(_storage, ticketKey)), "the original stays in quarantine");
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM notifications n JOIN users u ON u.id = n.user_id WHERE u.handle = 'staffer' AND n.kind = 'staff_redaction' AND n.subject_id = @ticketFile",
            new { ticketFile }));

        // Staff looked and approved: archived now.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE submission_files SET redaction_state = 'approved' WHERE id = @ticketFile", new { ticketFile });
        }

        await RunAsync(ticket);
        var approved = await FileAsync(ticketFile);
        Assert.Equal(("archived", "Archived after staff review."), (approved.State, approved.Note));
    }

    [Fact]
    public async Task An_unreadable_image_goes_to_staff_and_unverified_or_taken_down_submissions_are_left_alone()
    {
        var (broken, brokenFile, _) = await SubmissionAsync("merch", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var (pending, pendingFile, _) = await SubmissionAsync("ticket", Ticket(qr: true), status: "needs_review");
        var (removed, removedFile, _) = await SubmissionAsync("merch", Ticket(qr: true));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE submissions SET removed_at = now() WHERE id = @removed", new { removed });
        }

        await RunAsync(broken);
        await RunAsync(pending);
        await RunAsync(removed);

        var row = await FileAsync(brokenFile);
        Assert.Equal(("needs_review", "The image couldn't be read."), (row.State, row.Note));
        Assert.Equal("pending", (await FileAsync(pendingFile)).State);
        Assert.Equal("pending", (await FileAsync(removedFile)).State);
    }

    [Fact]
    public async Task A_failed_archive_write_keeps_the_original_quarantined_and_a_retry_finishes()
    {
        var (submission, file, key) = await SubmissionAsync("ticket", Ticket(qr: true));
        var failing = new RedactSubmissionJob(_db, new FailingStore(new LocalFileStore(_storage)), NullLogger<RedactSubmissionJob>.Instance);

        await Assert.ThrowsAsync<IOException>(() =>
            failing.RunAsync(new ClaimedJob(++_runs, failing.Kind, Payload(submission), 1, 10), CancellationToken.None));

        var row = await FileAsync(file);
        Assert.Equal(("pending", key, "hot"), (row.State, row.StorageKey, row.Tier));
        Assert.False(Directory.Exists(Path.Combine(_storage, "archive")) && Directory.EnumerateFiles(Path.Combine(_storage, "archive")).Any());

        await RunAsync(submission);
        Assert.Equal("archived", (await FileAsync(file)).State);
    }

    [Fact]
    public async Task The_hourly_sweep_queues_what_was_missed_and_warns_before_quarantine_deletes_it()
    {
        var (submission, _, _) = await SubmissionAsync("ticket", Ticket(qr: true));
        var (old, oldFile, _) = await SubmissionAsync("ticket", Ticket());
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM jobs;
                UPDATE submission_files SET uploaded_at = now() - interval '26 days', redaction_state = 'needs_review' WHERE id = @oldFile;
                """,
                new { oldFile });
        }

        var logger = new ListLogger<RedactionSweepJob>();
        var sweep = new RedactionSweepJob(_db, logger);
        Assert.Equal(("redaction.sweep", TimeSpan.FromHours(1)), (sweep.Kind, sweep.Every));
        await sweep.RunAsync(new ClaimedJob(++_runs, sweep.Kind, "{}", 1, 10), CancellationToken.None);

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM jobs WHERE kind = 'submissions.redact' AND payload->>'submission_id' = @id",
            new { id = submission.ToString() }));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM jobs WHERE payload->>'submission_id' = @id", new { id = old.ToString() }));
        var warning = Assert.Single(logger.Messages, m => m.Level == LogLevel.Warning);
        Assert.StartsWith("Redaction overdue: 1 verified files", warning.Text, StringComparison.Ordinal);
        Assert.Contains("(1 wait for staff)", warning.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private async Task RunAsync(Guid submission)
    {
        var job = new RedactSubmissionJob(_db, new LocalFileStore(_storage), NullLogger<RedactSubmissionJob>.Instance);
        await job.RunAsync(new ClaimedJob(++_runs, job.Kind, Payload(submission), 1, 10), CancellationToken.None);
    }

    private static string Payload(Guid submission) => $$"""{"submission_id":"{{submission}}"}""";

    /// <summary>A submission with one stored file, attached the way POST /submissions attaches it.</summary>
    private async Task<(Guid Submission, Guid File, string Key)> SubmissionAsync(string kind, byte[] content, string status = "verified")
    {
        var key = $"quarantine/{Guid.NewGuid()}";
        await new LocalFileStore(_storage).PutAsync(key, content, CancellationToken.None);
        await using var connection = await _db.OpenConnectionAsync();
        var submission = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO submissions (user_id, artist_id, kind) VALUES (@_fan, @_artistId, @kind::submission_kind) RETURNING id",
            new { _fan, _artistId, kind });
        var mime = content.Length > 3 && content[0] == 0x89 ? "image/png" : "image/jpeg";
        await connection.ExecuteAsync(
            """
            INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at, submission_id)
            VALUES (@_fan, 'submission', @mime, @bytes, @key, sha256(@key::bytea), @bytes, now(), @submission)
            """,
            new { _fan, mime, bytes = content.Length, key, submission });
        var file = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
            VALUES (@submission, @key, sha256(@key::bytea), @mime, @bytes) RETURNING id
            """,
            new { submission, key, mime, bytes = content.Length });
        if (status == "verified")
        {
            await VerifyAsync(submission);
        }
        else
        {
            await connection.ExecuteAsync("UPDATE submissions SET status = @status::submission_status WHERE id = @submission", new { status, submission });
        }

        return (submission, file, key);
    }

    private async Task VerifyAsync(Guid submission)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE submissions SET status = 'verified' WHERE id = @submission", new { submission });
    }

    private sealed record FileRow(string State, string Tier, bool Redacted, string StorageKey, int Codes, string? Note);

    private async Task<FileRow> FileAsync(Guid id)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<FileRow>(
            """
            SELECT redaction_state AS State, tier::text AS Tier, redacted AS Redacted, storage_key AS StorageKey,
                   COALESCE(codes_redacted, 0) AS Codes, redaction_note AS Note
            FROM submission_files WHERE id = @id
            """,
            new { id });
    }

    private async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, args);
    }

    /// <summary>A synthetic 1400×1000 ticket photo (JPEG) with the chosen codes on a beige background.</summary>
    internal static byte[] Ticket(
        bool qr = false, bool barcode = false, bool aztec = false, bool dataMatrix = false, bool pdf417 = false, float tilt = 0)
    {
        using var bitmap = new SKBitmap(1400, 1000);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(200, 190, 170));
            using var paint = new SKPaint { Color = new SKColor(60, 50, 40) };
            canvas.DrawRect(new SKRect(60, 900, 1340, 940), paint);   // a printed strip, not a code
            void Draw(BarcodeFormat format, string text, int width, int height, float x, float y)
            {
                using var code = new BarcodeWriter { Format = format, Options = new EncodingOptions { Width = width, Height = height, Margin = 2 } }.Write(text);
                using var image = SKImage.FromBitmap(code);
                canvas.DrawImage(image, x, y, new SKSamplingOptions(SKFilterMode.Nearest));
            }

            if (qr)
            {
                Draw(BarcodeFormat.QR_CODE, "TICKET-ORDER-1234567890", 250, 250, 80, 80);
            }

            if (barcode)
            {
                canvas.Save();
                canvas.RotateDegrees(tilt, 1000, 300);
                Draw(BarcodeFormat.CODE_128, "ORD99887766", 420, 110, 800, 250);
                canvas.Restore();
            }

            if (aztec)
            {
                Draw(BarcodeFormat.AZTEC, "AZ-77", 200, 200, 1100, 650);
            }

            if (dataMatrix)
            {
                Draw(BarcodeFormat.DATA_MATRIX, "DM-55", 160, 160, 100, 700);
            }

            if (pdf417)
            {
                Draw(BarcodeFormat.PDF_417, "SEC 101 ROW A SEAT 5", 300, 100, 450, 120);
            }
        }

        using var encoded = SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>Codes an independent scan (whole image, then 3×3 sections) still reads.</summary>
    private static List<string> Decode(byte[] content)
    {
        using var bitmap = SKBitmap.Decode(content);
        var reader = new BarcodeReader { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, TryInverted = true } };
        var found = (reader.DecodeMultiple(bitmap) ?? []).Select(r => r.Text).ToList();
        for (var gx = 0; gx < 3; gx++)
        {
            for (var gy = 0; gy < 3; gy++)
            {
                using var subset = new SKBitmap();
                bitmap.ExtractSubset(subset, SKRectI.Create(gx * bitmap.Width / 4, gy * bitmap.Height / 4, bitmap.Width / 2, bitmap.Height / 2));
                using var tile = subset.Copy();
                found.AddRange((reader.DecodeMultiple(tile) ?? []).Select(r => r.Text));
            }
        }

        return [.. found.Distinct()];
    }

    private sealed class FailingStore(IFileStore inner) : IFileStore
    {
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            key.StartsWith("archive/", StringComparison.Ordinal)
                ? throw new IOException("storage unavailable")
                : inner.PutAsync(key, content, cancellationToken);

        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken) => inner.ReadAsync(key, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
