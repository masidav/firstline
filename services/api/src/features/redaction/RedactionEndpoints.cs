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
/// it (the file is deleted; the submission keeps its status and points).
/// </summary>
public static class RedactionEndpoints
{
    /// <summary>The quarantine bucket deletes files this long after upload (decided 2026-10-25).</summary>
    public static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(30);

    private const int MaxItems = 100;

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
            // The worker archives an approved file after redacting whatever it finds (RedactSubmissionJob).
            await connection.ExecuteAsync(
                """
                UPDATE submission_files SET redaction_state = 'approved' WHERE id = @fileId;
                SELECT enqueue_job('submissions.redact', jsonb_build_object('submission_id', @submissionId), 'redact:' || @submissionId);
                """,
                new { fileId, submissionId = file.SubmissionId }, transaction);
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

    private sealed record Row(
        Guid FileId, Guid SubmissionId, string Kind, Guid ArtistId, string ArtistName, string? ArtistImageUrl, string? Handle,
        bool Deleted, string? DisplayName, string? Note, int CodesRedacted, DateTime UploadedAt, string MimeType);
}
