using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FandomScore.Storage;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.ImageRedaction;

/// <summary>
/// <c>submissions.redact</c> (payload <c>{"submission_id": ...}</c>): redacts a verified submission's files and moves
/// them to the archive (migration 0029). Queued when a submission becomes verified, by staff approving a file, and by
/// <see cref="RedactionSweepJob"/>. For each file still <c>pending</c> or <c>approved</c>:
/// <list type="number">
/// <item>Paint black any boxes staff drew over the file (<c>staff_boxes</c>, migration 0056), then black out every
/// barcode and QR code <see cref="CodeRedactor"/> finds, and scan again.</item>
/// <item>On a ticket or screenshot, when OCR is configured (<see cref="RedactionOptions"/>): read the printed
/// words, black out order and confirmation numbers (<see cref="OrderNumberFinder"/>), and record counts only in
/// <c>submissions.extracted</c> and <c>submission_files.text_redacted</c>. The first number's hash becomes
/// <c>submissions.order_ref_hash</c>; when another verified submission already carries it, the file goes to staff
/// instead (migration 0061: one order, one fan).</item>
/// <item>Send it to staff (<c>needs_review</c>) when the image can't be read, codes are still found, or a ticket or
/// screenshot shows no code at all (a ticket nearly always has one, so the scanner probably missed it). Staff
/// approving a file means they looked: it is then archived whatever the scan says, after painting their boxes and
/// redacting what it finds.</item>
/// <item>Otherwise write the redacted copy to <c>archive/{file id}</c>, check it reads back, and point the file (and its
/// upload slot) at it in one transaction. The old quarantine key is queued for deletion by migration 0027.</item>
/// </list>
/// Only verified submissions that weren't taken down are archived. The original never leaves quarantine unredacted,
/// and a failure (storage, a crash) leaves it there for the next attempt: the archive key is fixed per file, so a
/// retry overwrites rather than duplicates.
/// </summary>
public sealed partial class RedactSubmissionJob(
    NpgsqlDataSource db, IFileStore files, ILogger<RedactSubmissionJob> logger, ITextFinder? textFinder = null) : IJobHandler
{
    public const string JobKind = "submissions.redact";

    /// <summary>Kinds whose archive is staff-only proof and may carry printed order numbers.</summary>
    public static readonly IReadOnlyList<string> OcrKinds = ["ticket", "screenshot"];   // `receipt` retired 2026-11-07 (0062)

    private readonly ITextFinder _textFinder = textFinder ?? NoTextFinder.Instance;

    public string Kind => JobKind;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("submission_id", out var idElement) || !idElement.TryGetGuid(out var submissionId))
        {
            LogBadPayload(logger, job.Id);
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var kind = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT kind::text FROM submissions WHERE id = @submissionId AND status = 'verified' AND removed_at IS NULL",
            new { submissionId });
        if (kind is null)
        {
            return;   // gone, not verified (any more), or taken down: nothing to archive
        }

        var fileIds = await connection.QueryAsync<Guid>(
            """
            SELECT id FROM submission_files
            WHERE submission_id = @submissionId AND redaction_state IN ('pending', 'approved')
            ORDER BY uploaded_at, id
            """,
            new { submissionId });
        foreach (var fileId in fileIds)
        {
            await RedactFileAsync(connection, submissionId, fileId, kind, cancellationToken);
        }

        // The submission counts as archived once every file is.
        await connection.ExecuteAsync(
            """
            UPDATE submissions SET archived_at = now()
            WHERE id = @submissionId AND archived_at IS NULL
              AND EXISTS (SELECT 1 FROM submission_files WHERE submission_id = @submissionId)
              AND NOT EXISTS (SELECT 1 FROM submission_files WHERE submission_id = @submissionId AND redaction_state <> 'archived')
            """,
            new { submissionId });
    }

    private async Task RedactFileAsync(NpgsqlConnection connection, Guid submissionId, Guid fileId, string kind, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var file = await connection.QuerySingleOrDefaultAsync<FileRow>(
            """
            SELECT storage_key AS StorageKey, mime_type AS MimeType, redaction_state = 'approved' AS Approved,
                   staff_boxes::text AS StaffBoxes
            FROM submission_files WHERE id = @fileId AND redaction_state IN ('pending', 'approved')
            FOR UPDATE
            """,
            new { fileId }, transaction);
        if (file is null)
        {
            return;   // already archived, sent to staff, or deleted meanwhile
        }

        var original = await files.ReadAsync(file.StorageKey, cancellationToken);
        if (original is null)
        {
            await NeedsReviewAsync(connection, transaction, fileId, "The stored file is missing.", 0, cancellationToken);
            return;
        }

        // Boxes are honoured only on a file staff approved: a pending file never has any, and if one somehow did,
        // a decision staff didn't just make shouldn't shape the archive.
        var boxes = file.Approved ? StaffBox.Parse(file.StaffBoxes) : [];
        var result = CodeRedactor.Redact(original, file.MimeType, boxes);
        var problem = result switch
        {
            { Unreadable: true } => "The image couldn't be read.",
            _ when !IsReadable(result.Content!) => "The redacted copy couldn't be read back.",
            { CodesRemaining: > 0 } when !file.Approved =>
                $"Codes were still found after {CodeRedactor.MaxPasses} rounds of redacting.",
            { CodesRedacted: 0 } when !file.Approved && kind is ("ticket" or "screenshot") =>
                $"No barcode or QR code was found on this {kind}. Check it by eye.",
            _ => null,
        };
        if (problem is not null)
        {
            await NeedsReviewAsync(connection, transaction, fileId, problem, result.CodesRedacted, cancellationToken);
            return;
        }

        var content = result.Content!;
        int? textRedacted = null;
        if (_textFinder.Enabled && OcrKinds.Contains(kind))
        {
            var ocr = await RedactPrintedNumbersAsync(connection, transaction, submissionId, kind, content, file.MimeType, cancellationToken);
            if (ocr.DuplicateOf is { } other)
            {
                await NeedsReviewAsync(connection, transaction, fileId,
                    "The printed order number matches another verified submission. Check both by eye.", result.CodesRedacted, cancellationToken);
                LogDuplicateOrder(logger, fileId, other);
                return;
            }

            content = ocr.Content ?? content;
            textRedacted = ocr.NumbersRedacted;
        }

        var archiveKey = $"archive/{fileId:N}{(file.MimeType == "image/png" ? ".png" : ".jpg")}";
        await files.PutAsync(archiveKey, content, cancellationToken);
        try
        {
            // sha256 stays the original's: duplicate detection compares new uploads with what fans sent.
            await connection.ExecuteAsync(
                """
                UPDATE submission_files
                SET storage_key = @archiveKey, tier = 'archive', redacted = true, archived_at = now(), bytes = @bytes,
                    redaction_state = 'archived', codes_redacted = @codes, text_redacted = @textRedacted,
                    redaction_note = CASE
                        WHEN redaction_state = 'approved' AND @boxes > 0
                            THEN 'Archived after staff review; ' || @boxes || ' staff-drawn ' || CASE WHEN @boxes = 1 THEN 'box' ELSE 'boxes' END || ' blacked out.'
                        WHEN redaction_state = 'approved' THEN 'Archived after staff review.'
                    END
                WHERE id = @fileId;
                UPDATE uploads SET storage_key = @archiveKey, sha256 = @sha, bytes = @bytes WHERE storage_key = @oldKey;
                """,
                new
                {
                    fileId, archiveKey, oldKey = file.StorageKey, bytes = content.Length, sha = SHA256.HashData(content),
                    codes = result.CodesRedacted, boxes = result.BoxesPainted, textRedacted,
                },
                transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The row still names the quarantined original; don't leave a redacted copy no row names.
            await files.DeleteAsync(archiveKey, CancellationToken.None);
            throw;
        }

        LogArchived(logger, fileId, result.CodesRedacted, result.BoxesPainted, textRedacted ?? 0, file.Approved);
    }

    private sealed record OcrOutcome(byte[]? Content, int? NumbersRedacted, Guid? DuplicateOf);

    /// <summary>
    /// OCR over the code-redacted copy: blacks out printed order numbers, records counts (never the numbers) in
    /// <c>submissions.extracted</c> and a <c>ocr_extract</c> check, and sets <c>order_ref_hash</c> from the first number
    /// unless another verified submission of the same kind already carries it (then <c>DuplicateOf</c>, with an
    /// <c>order_ref</c> check that failed). A null <c>NumbersRedacted</c> means OCR didn't run (unavailable or failed).
    /// </summary>
    private async Task<OcrOutcome> RedactPrintedNumbersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid submissionId, string kind, byte[] content, string mimeType,
        CancellationToken cancellationToken)
    {
        var words = await _textFinder.FindWordsAsync(content, cancellationToken);
        if (words is null)
        {
            return new OcrOutcome(null, null, null);
        }

        var numbers = OrderNumberFinder.Find(words);
        var painted = numbers.Count == 0 ? content : CodeRedactor.PaintBoxes(content, mimeType, [.. numbers.Select(n => n.Box)]) ?? content;
        await connection.ExecuteAsync(
            """
            UPDATE submissions
            SET extracted = extracted || jsonb_build_object('ocr', 'tesseract', 'words', @words, 'order_numbers_redacted', @numbers, 'checked_at', now())
            WHERE id = @submissionId;
            INSERT INTO verification_checks (submission_id, check_type, result, details)
            VALUES (@submissionId, 'ocr_extract', @result::check_result, jsonb_build_object('words', @words, 'order_numbers', @numbers));
            """,
            new { submissionId, words = words.Count, numbers = numbers.Count, result = numbers.Count > 0 ? "pass" : "inconclusive" }, transaction);

        if (numbers.Count == 0)
        {
            return new OcrOutcome(painted, 0, null);
        }

        var hash = OrderNumberFinder.Hash(numbers[0].Normalized);
        var duplicateOf = await connection.ExecuteScalarAsync<Guid?>(
            """
            SELECT id FROM submissions
            WHERE kind::text = @kind AND order_ref_hash = @hash AND status = 'verified' AND id <> @submissionId
            ORDER BY verified_at LIMIT 1
            """,
            new { kind, hash, submissionId }, transaction);
        if (duplicateOf is { } other)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO verification_checks (submission_id, check_type, result, details)
                VALUES (@submissionId, 'order_ref', 'fail', jsonb_build_object('duplicate_of', @other));
                """,
                new { submissionId, other }, transaction);
            return new OcrOutcome(null, numbers.Count, other);
        }

        await connection.ExecuteAsync(
            "UPDATE submissions SET order_ref_hash = COALESCE(order_ref_hash, @hash) WHERE id = @submissionId",
            new { submissionId, hash }, transaction);
        return new OcrOutcome(painted, numbers.Count, null);
    }

    private async Task NeedsReviewAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fileId, string note, int codes, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            "UPDATE submission_files SET redaction_state = 'needs_review', redaction_note = @note, codes_redacted = @codes WHERE id = @fileId",
            new { fileId, note, codes }, transaction);
        await transaction.CommitAsync(cancellationToken);
        LogNeedsReview(logger, fileId, note);
    }

    private static bool IsReadable(byte[] content)
    {
        using var bitmap = CodeRedactor.TryDecode(content);
        return bitmap is not null;
    }

    private sealed record FileRow(string StorageKey, string MimeType, bool Approved, string? StaffBoxes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: payload has no submission_id.")]
    private static partial void LogBadPayload(ILogger logger, long jobId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Archived file {FileId}: {Codes} codes redacted, {Boxes} staff boxes painted, {Numbers} printed numbers blacked out (staff approved: {Approved}).")]
    private static partial void LogArchived(ILogger logger, Guid fileId, int codes, int boxes, int numbers, bool approved);

    [LoggerMessage(Level = LogLevel.Information, Message = "File {FileId} needs staff review: {Note}")]
    private static partial void LogNeedsReview(ILogger logger, Guid fileId, string note);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File {FileId}: its printed order number matches verified submission {Other}; sent to staff.")]
    private static partial void LogDuplicateOrder(ILogger logger, Guid fileId, Guid other);
}
