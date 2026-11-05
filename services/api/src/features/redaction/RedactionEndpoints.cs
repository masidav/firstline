using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using FandomScore.Storage;
using Npgsql;

namespace FandomScore.Api.Features.Redaction;

/// <summary>
/// Staff decisions on files the redaction worker couldn't settle (migration 0029, workers/image-redaction): a ticket or
/// screenshot with no code found, codes still found after redacting, or an image that can't be read. Staff look at the
/// file and either archive it (the worker redacts whatever it finds and archives it, trusting their look) or discard
/// it (the file is deleted; the submission keeps its status and points). Archiving can carry boxes staff drew over
/// what the scanner missed (a printed order number); the worker paints them black first (migration 0056).
/// </summary>
public static class RedactionEndpoints
{
    /// <summary>The quarantine bucket deletes files this long after upload (decided 2026-10-25).</summary>
    public static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(30);

    private const int MaxItems = 100;

    /// <summary>Boxes staff may draw on one file (the database checks the same bound, migration 0056).</summary>
    public const int MaxBoxes = 20;

    private static readonly JsonSerializerOptions BoxJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapRedaction(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/redactions", QueueAsync);
        admin.MapPost("/redactions/{fileId:guid}/decision", DecideAsync);
    }

    public sealed record RedactionItem(
        Guid FileId, Guid SubmissionId, string Kind, ScoringEndpoints.ArtistRef Artist, SubmissionEndpoints.UserRef User,
        string? Note, int CodesRedacted, DateTime UploadedAt, DateTime DeletedAfter, SubmissionEndpoints.SubmissionFile File);

    public sealed record RedactionList(IReadOnlyList<RedactionItem> Items);

    private static async Task<IResult> QueueAsync(
        HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(
            """
            SELECT f.id AS FileId, s.id AS SubmissionId, s.kind::text AS Kind, a.id AS ArtistId, a.name AS ArtistName,
                   a.image_url AS ArtistImageUrl, u.handle AS Handle, u.deleted_at IS NOT NULL AS Deleted,
                   u.display_name AS DisplayName, f.redaction_note AS Note, COALESCE(f.codes_redacted, 0) AS CodesRedacted,
                   f.uploaded_at AS UploadedAt, f.mime_type AS MimeType
            FROM submission_files f
            JOIN submissions s ON s.id = f.submission_id
            JOIN artists a ON a.id = s.artist_id
            JOIN users u ON u.id = s.user_id
            WHERE f.redaction_state = 'needs_review'
            ORDER BY f.uploaded_at, f.id
            LIMIT @MaxItems
            """,
            new { MaxItems });
        return Results.Ok(new RedactionList([.. rows.Select(r => new RedactionItem(
            r.FileId, r.SubmissionId, r.Kind, new ScoringEndpoints.ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl),
            new SubmissionEndpoints.UserRef(r.Deleted ? null : r.Handle, r.Deleted, r.Deleted ? null : r.DisplayName, null),
            r.Note, r.CodesRedacted, r.UploadedAt, r.UploadedAt + QuarantineRetention,
            new SubmissionEndpoints.SubmissionFile(r.FileId, r.MimeType, UploadEndpoints.FileUrl(request, urls, r.FileId), false)))]));
    }

    private static async Task<IResult> DecideAsync(
        Guid fileId, JsonElement body, NpgsqlDataSource db, IFileStore files, CancellationToken cancellationToken)
    {
        var action = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("action", out var value)
                     && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (action is not ("archive" or "discard"))
        {
            throw ApiProblemException.Validation(new FieldError("action", "Send archive or discard."));
        }

        var boxes = ParseBoxes(body, action);

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var file = await connection.QuerySingleOrDefaultAsync<(Guid SubmissionId, string StorageKey, string State)>(
            "SELECT submission_id, storage_key, redaction_state FROM submission_files WHERE id = @fileId FOR UPDATE",
            new { fileId }, transaction);
        if (file.SubmissionId == Guid.Empty)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "File not found", "No file has this id.");
        }

        if (file.State != "needs_review")
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Not waiting for review",
                "This file isn't waiting for a redaction decision.");
        }

        if (action == "archive")
        {
            // The worker archives an approved file after painting the boxes and redacting whatever it finds
            // (RedactSubmissionJob). The boxes replace any from an earlier decision on the same file.
            await connection.ExecuteAsync(
                """
                UPDATE submission_files SET redaction_state = 'approved', staff_boxes = @boxes::jsonb WHERE id = @fileId;
                SELECT enqueue_job('submissions.redact', jsonb_build_object('submission_id', @submissionId), 'redact:' || @submissionId);
                """,
                new { fileId, submissionId = file.SubmissionId, boxes = boxes.Count == 0 ? null : JsonSerializer.Serialize(boxes, BoxJson) },
                transaction);
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        }

        // Discard: the file row and its upload slot go, and the database queues the key (migration 0027). The stored
        // file goes now; if that fails, the worker's storage.cleanup deletes it.
        await connection.ExecuteAsync(
            """
            DELETE FROM submission_files WHERE id = @fileId;
            DELETE FROM uploads WHERE storage_key = @key;
            """,
            new { fileId, key = file.StorageKey }, transaction);
        await transaction.CommitAsync(cancellationToken);
        try
        {
            await files.DeleteAsync(file.StorageKey, cancellationToken);
            await connection.ExecuteAsync("DELETE FROM storage_deletions WHERE storage_key = @key", new { key = file.StorageKey });
        }
        catch (IOException)
        {
            // Discarded either way; storage.cleanup retries the queued key.
        }

        return Results.NoContent();
    }

    /// <summary>A box staff drew, as fractions of the image from its top-left corner (openapi.yaml RedactionBox).</summary>
    public sealed record Box(double X, double Y, double W, double H);

    /// <summary>
    /// The decision's <c>boxes</c>, validated: optional, only with <c>archive</c>, at most <see cref="MaxBoxes"/>,
    /// each with finite x, y, w, h that keep it inside the image (x, y ≥ 0; w, h > 0; x + w ≤ 1; y + h ≤ 1).
    /// </summary>
    private static List<Box> ParseBoxes(JsonElement body, string action)
    {
        if (!body.TryGetProperty("boxes", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (action != "archive")
        {
            throw ApiProblemException.Validation(new FieldError("boxes", "Boxes go with archive: a discarded file is deleted whole."));
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw ApiProblemException.Validation(new FieldError("boxes", "Send a list of boxes."));
        }

        var boxes = new List<Box>();
        foreach (var item in element.EnumerateArray())
        {
            var field = $"boxes[{boxes.Count}]";
            if (boxes.Count == MaxBoxes)
            {
                throw ApiProblemException.Validation(new FieldError("boxes", $"At most {MaxBoxes} boxes."));
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw ApiProblemException.Validation(new FieldError(field, "A box is an object with x, y, w, and h."));
            }

            var box = new Box(Side(item, "x", field), Side(item, "y", field), Side(item, "w", field), Side(item, "h", field));
            if (box.W <= 0 || box.H <= 0)
            {
                throw ApiProblemException.Validation(new FieldError(field, "A box needs a width and a height."));
            }

            if (box.X + box.W > 1 || box.Y + box.H > 1)
            {
                throw ApiProblemException.Validation(new FieldError(field, "A box must stay inside the image (x + w and y + h at most 1)."));
            }

            boxes.Add(box);
        }

        return boxes;
    }

    private static double Side(JsonElement box, string name, string field)
    {
        if (!box.TryGetProperty(name, out var side) || side.ValueKind != JsonValueKind.Number || !side.TryGetDouble(out var n)
            || !double.IsFinite(n) || n < 0 || n > 1)
        {
            throw ApiProblemException.Validation(new FieldError($"{field}.{name}", "A number from 0 to 1 (a fraction of the image)."));
        }

        return n;
    }

    private sealed record Row(
        Guid FileId, Guid SubmissionId, string Kind, Guid ArtistId, string ArtistName, string? ArtistImageUrl, string? Handle,
        bool Deleted, string? DisplayName, string? Note, int CodesRedacted, DateTime UploadedAt, string MimeType);
}
