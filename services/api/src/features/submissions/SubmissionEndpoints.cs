using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Infrastructure;
using FandomScore.Storage;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.Submissions;

/// <summary>
/// Proof of fandom (openapi <c>/submissions</c>, <c>/me/submissions</c>, sharing, and the staff review queue).
/// There are no automated checks yet, so every submission goes straight to <c>needs_review</c> for staff. Approval
/// awards points (<c>&lt;kind&gt;_verified</c>, database trigger); a later rejection reverses them and takes the
/// photo off the feed. Only verified merch and concert photos can be shared.
/// </summary>
public static class SubmissionEndpoints
{
    public static readonly IReadOnlyList<string> Kinds = ["ticket", "merch", "screenshot", "concert_photo"];
    public static readonly IReadOnlyList<string> ShareableKinds = ["merch", "concert_photo"];
    public const int DailySubmissions = 20;
    public const int CaptionMaxLength = 500;

    public static void MapSubmissions(this RouteGroupBuilder v1)
    {
        v1.MapPost("/submissions", CreateAsync);
        v1.MapGet("/me/submissions", ListMineAsync);
        v1.MapGet("/submissions/{submissionId:guid}", GetAsync);
        v1.MapDelete("/submissions/{submissionId:guid}", WithdrawAsync);
        v1.MapPut("/submissions/{submissionId:guid}/share", ShareAsync);

        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/reviews", ReviewQueueAsync);
        admin.MapGet("/submissions/{submissionId:guid}", GetForReviewAsync);
        admin.MapPost("/submissions/{submissionId:guid}/decision", DecideAsync);
    }

    public sealed record SubmissionFile(Guid Id, string MimeType, string Url, bool Redacted);

    public sealed record PointsAward(string RuleKey, decimal Points);

    /// <summary>The listed show a submission is pinned to (openapi EventRef).</summary>
    public sealed record EventRef(Guid Id, string Name, string? Venue, string? City, DateTime StartsAt);

    /// <summary>A show the fan typed in because it wasn't listed.</summary>
    public sealed record Concert(DateOnly Date, string Venue, string City);

    public sealed record Submission(
        Guid Id, Guid ArtistId, ScoringEndpoints.ArtistRef Artist, string Kind, string Status, EventRef? Event,
        Concert? Concert, string? RejectionReason, DateTime SubmittedAt, DateTime? VerifiedAt, bool Archived, string? Caption,
        DateTime? SharedAt, bool CanShare, bool Removed, IReadOnlyList<SubmissionFile> Files, IReadOnlyList<PointsAward> PointsAwarded);

    public sealed record SubmissionPage(IReadOnlyList<Submission> Items, string? NextCursor);

    public sealed record UserRef(string? Handle, bool Deleted, string? DisplayName, string? AvatarUrl);

    public sealed record VerificationCheck(
        Guid Id, string CheckType, string Result, decimal? Confidence, JsonElement Details, Guid? ReviewerId, DateTime CreatedAt);

    public sealed record AdminSubmission(
        Guid Id, Guid ArtistId, ScoringEndpoints.ArtistRef Artist, string Kind, string Status, EventRef? Event,
        Concert? Concert, string? RejectionReason, DateTime SubmittedAt, DateTime? VerifiedAt, bool Archived, string? Caption,
        DateTime? SharedAt, bool CanShare, bool Removed, IReadOnlyList<SubmissionFile> Files, IReadOnlyList<PointsAward> PointsAwarded,
        UserRef User, decimal? TrustScore, JsonElement Extracted, IReadOnlyList<VerificationCheck> Checks);

    public sealed record AdminSubmissionPage(IReadOnlyList<AdminSubmission> Items, string? NextCursor);

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user, HttpRequest request, JsonElement body, NpgsqlDataSource db, SignedUrls urls, TimeProvider time,
        IOptions<JsonOptions> json, CancellationToken cancellationToken)
    {
        var key = Idempotency.ReadKey(request);
        var (artistId, kind, uploadIds, caption, eventId, concert) = ParseCreate(body, time);
        var userId = user.UserId();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (key is not null
            && await Idempotency.BeginAsync(connection, transaction, userId, key, Idempotency.Hash(body.GetRawText())) is { } replay)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay.ToResult();
        }

        // One submission at a time per user, so the daily limit and duplicate check can't be raced.
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('submission:' || @userId::text, 0))",
            new { userId }, transaction);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId AND status IN ('onboarding', 'active'))",
                new { artistId }, transaction))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Artist not found", "No tracked artist has this id.");
        }

        // A pinned show must be this artist's, and already have happened (a day of slack for time zones).
        if (eventId is not null && !await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM events WHERE id = @eventId AND artist_id = @artistId AND starts_at <= now() + interval '1 day')",
                new { eventId, artistId }, transaction))
        {
            throw ApiProblemException.Validation(new FieldError("event_id", "Choose one of this artist's past shows."));
        }

        if (await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM submissions WHERE user_id = @userId AND submitted_at >= now() - interval '1 day'",
                new { userId }, transaction) >= DailySubmissions)
        {
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many submissions",
                $"You can send {DailySubmissions} a day. Try again tomorrow.") { RetryAfter = TimeSpan.FromHours(1) };
        }

        var uploads = (await connection.QueryAsync<(Guid Id, string StorageKey, byte[] Sha256, string MimeType, int Bytes)>(
            """
            SELECT id, storage_key, sha256, mime_type, bytes FROM uploads
            WHERE id = ANY(@uploadIds) AND user_id = @userId AND uploaded_at IS NOT NULL AND submission_id IS NULL
            FOR UPDATE
            """,
            new { uploadIds, userId }, transaction)).ToList();
        if (uploads.Count != uploadIds.Length)
        {
            throw ApiProblemException.Validation(new FieldError("upload_ids",
                "Each upload must be yours, finished, and not already used in another submission."));
        }

        // The same photo sent again by the same fan (unless it was rejected) is a duplicate.
        if (await connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (SELECT 1 FROM submission_files f JOIN submissions s ON s.id = f.submission_id
                               WHERE s.user_id = @userId AND s.status <> 'rejected' AND f.sha256 = ANY(@hashes))
                """,
                new { userId, hashes = uploads.Select(u => u.Sha256).ToArray() }, transaction))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "duplicate_submission", "Already submitted",
                "You've already sent this photo.");
        }

        var submissionId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO submissions (user_id, artist_id, kind, status, caption, event_id, concert_date, concert_venue, concert_city)
            VALUES (@userId, @artistId, @kind::submission_kind, 'needs_review', @caption, @eventId, @date, @venue, @city) RETURNING id
            """,
            new { userId, artistId, kind, caption, eventId, date = concert?.Date, venue = concert?.Venue, city = concert?.City },
            transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
            SELECT @submissionId, u.storage_key, u.sha256, u.mime_type, u.bytes FROM uploads u WHERE u.id = ANY(@uploadIds);
            UPDATE uploads SET submission_id = @submissionId WHERE id = ANY(@uploadIds);
            """,
            new { submissionId, uploadIds }, transaction);

        var submission = (await LoadAsync(connection, transaction, request, urls, submissionId))!;
        if (key is not null)
        {
            await Idempotency.CompleteAsync(connection, transaction, userId, key, StatusCodes.Status201Created,
                JsonSerializer.Serialize(submission, json.Value.SerializerOptions));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/v1/submissions/{submissionId}", submission);
    }

    private static (Guid ArtistId, string Kind, Guid[] UploadIds, string? Caption, Guid? EventId, Concert? Concert) ParseCreate(
        JsonElement body, TimeProvider time)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var errors = new List<FieldError>();
        var artistId = body.TryGetProperty("artist_id", out var a) && a.ValueKind == JsonValueKind.String && a.TryGetGuid(out var g) ? g : Guid.Empty;
        if (artistId == Guid.Empty)
        {
            errors.Add(new FieldError("artist_id", "Choose the artist."));
        }

        var kind = body.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
        if (!Kinds.Contains(kind))
        {
            errors.Add(new FieldError("kind", "Use ticket, merch, screenshot, or concert_photo."));
        }

        var uploadIds = new List<Guid>();
        if (body.TryGetProperty("upload_ids", out var u) && u.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in u.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.TryGetGuid(out var id)) uploadIds.Add(id);
                else errors.Add(new FieldError("upload_ids", "Send upload ids from POST /uploads."));
            }
        }

        if (uploadIds.Distinct().Count() is < 1 or > 5 || uploadIds.Distinct().Count() != uploadIds.Count)
        {
            errors.Add(new FieldError("upload_ids", "Send 1 to 5 different upload ids."));
        }

        string? caption = null;
        if (body.TryGetProperty("caption", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            caption = c.ValueKind == JsonValueKind.String ? c.GetString()!.Trim() : null;
            if (caption is null || caption.Length > CaptionMaxLength)
            {
                errors.Add(new FieldError("caption", $"Use up to {CaptionMaxLength} characters."));
            }

            caption = string.IsNullOrEmpty(caption) ? null : caption;
        }

        Guid? eventId = null;
        if (body.TryGetProperty("event_id", out var e) && e.ValueKind != JsonValueKind.Null)
        {
            if (e.ValueKind == JsonValueKind.String && e.TryGetGuid(out var id)) eventId = id;
            else errors.Add(new FieldError("event_id", "Send the id of one of this artist's shows."));
        }

        var concert = ParseConcert(body, time, errors);
        if (concert is not null && kind != "concert_photo")
        {
            errors.Add(new FieldError("concert", "Only concert photos take a concert."));
        }

        if (eventId is not null && concert is not null)
        {
            errors.Add(new FieldError("concert", "Send event_id or concert, not both."));
        }

        if (kind == "concert_photo" && eventId is null && concert is null && !errors.Any(x => x.Field.StartsWith("concert", StringComparison.Ordinal)))
        {
            errors.Add(new FieldError("event_id", "Pin the photo to its concert: choose the show, or enter its date, venue, and city."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        return (artistId, kind, [.. uploadIds], caption, eventId, concert);
    }

    private static readonly DateOnly EarliestConcert = new(1950, 1, 1);

    private static Concert? ParseConcert(JsonElement body, TimeProvider time, List<FieldError> errors)
    {
        if (!body.TryGetProperty("concert", out var c) || c.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string? Text(string name) =>
            c.ValueKind == JsonValueKind.Object && c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!.Trim() : null;

        var dateText = Text("date");
        var venue = Text("venue");
        var city = Text("city");
        var latest = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1);
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", out var date) || date < EarliestConcert || date > latest)
        {
            errors.Add(new FieldError("concert.date", "Enter the concert date (YYYY-MM-DD); it can't be in the future."));
        }

        if (string.IsNullOrEmpty(venue) || venue.Length > 200)
        {
            errors.Add(new FieldError("concert.venue", "Enter the venue (up to 200 characters)."));
        }

        if (string.IsNullOrEmpty(city) || city.Length > 100)
        {
            errors.Add(new FieldError("concert.city", "Enter the city (up to 100 characters)."));
        }

        return errors.Any(x => x.Field.StartsWith("concert.", StringComparison.Ordinal)) ? null : new Concert(date, venue!, city!);
    }

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal user, Guid? artist_id, string? status, int? limit, string? cursor, HttpRequest request,
        NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (status is not null and not ("pending" or "processing" or "needs_review" or "verified" or "rejected"))
        {
            errors.Add(new FieldError("status", "Use pending, processing, needs_review, verified, or rejected."));
        }

        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = (await connection.QueryAsync<Guid>(
            """
            SELECT id FROM submissions
            WHERE user_id = @userId AND (@artistId::uuid IS NULL OR artist_id = @artistId)
              AND (@status::text IS NULL OR status::text = @status)
            ORDER BY submitted_at DESC, id LIMIT @take OFFSET @offset
            """,
            new { userId = user.UserId(), artistId = artist_id, status, take = pageSize + 1, offset })).ToList();
        var items = new List<Submission>();
        foreach (var id in ids.Take(pageSize))
        {
            items.Add((await LoadAsync(connection, null, request, urls, id))!);
        }

        return Results.Ok(new SubmissionPage(items, ids.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, Guid submissionId, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadMineAsync(connection, user.UserId(), request, urls, submissionId));
    }

    private static async Task<IResult> WithdrawAsync(
        ClaimsPrincipal user, Guid submissionId, NpgsqlDataSource db, IFileStore files, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT status::text FROM submissions WHERE id = @submissionId AND user_id = @userId",
            new { submissionId, userId = user.UserId() }) ?? throw NotFound();
        if (status == "verified")
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already verified",
                "Verified submissions can't be withdrawn.");
        }

        // Deleting the row removes its file rows and upload slots (cascade), and the database queues their keys in
        // storage_deletions (migration 0027). The stored files go now; if that fails, the queue keeps their keys and the
        // worker's storage.cleanup job deletes them.
        var keys = (await connection.QueryAsync<string>(
            "SELECT storage_key FROM submission_files WHERE submission_id = @submissionId", new { submissionId })).ToList();
        if (await connection.ExecuteAsync(
                "DELETE FROM submissions WHERE id = @submissionId AND status <> 'verified'", new { submissionId }) == 0)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already verified",
                "Verified submissions can't be withdrawn.");
        }

        try
        {
            foreach (var key in keys)
            {
                await files.DeleteAsync(key, cancellationToken);
            }

            await connection.ExecuteAsync("DELETE FROM storage_deletions WHERE storage_key = ANY(@keys)", new { keys = keys.ToArray() });
        }
        catch (IOException)
        {
            // The submission is withdrawn either way; storage.cleanup retries the files still queued.
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ShareAsync(
        ClaimsPrincipal user, Guid submissionId, JsonElement body, HttpRequest request, NpgsqlDataSource db, SignedUrls urls,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("shared", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ApiProblemException.Validation(new FieldError("shared", "Send true to share or false to stop sharing."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var current = await LoadMineAsync(connection, userId, request, urls, submissionId);
        if (value.GetBoolean() && !current.CanShare)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Can't share this",
                current.Removed ? "Staff took this photo down, so it can't be shared."
                : ShareableKinds.Contains(current.Kind)
                    ? "Only verified photos can be shared. This one is still being checked."
                    : "Tickets and screenshots can't be shared; they can show barcodes or personal details.");
        }

        // Re-checked in the UPDATE: staff may have rejected it since it was loaded.
        if (await connection.ExecuteAsync(
                """
                UPDATE submissions SET shared_at = CASE WHEN @shared THEN COALESCE(shared_at, now()) END
                WHERE id = @submissionId AND (NOT @shared OR (status = 'verified' AND removed_at IS NULL))
                """,
                new { submissionId, shared = value.GetBoolean() }) == 0)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Can't share this",
                "This photo can't be shared: it's no longer verified, or staff took it down.");
        }
        return Results.Ok(await LoadMineAsync(connection, userId, request, urls, submissionId));
    }

    private static async Task<IResult> ReviewQueueAsync(
        int? limit, string? cursor, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw ApiProblemException.Validation(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            throw ApiProblemException.Validation(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = (await connection.QueryAsync<Guid>(
            "SELECT id FROM submissions WHERE status = 'needs_review' ORDER BY submitted_at, id LIMIT @take OFFSET @offset",
            new { take = pageSize + 1, offset })).ToList();
        var items = new List<AdminSubmission>();
        foreach (var id in ids.Take(pageSize))
        {
            items.Add((await LoadForReviewAsync(connection, null, request, urls, id))!);
        }

        return Results.Ok(new AdminSubmissionPage(items, ids.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> GetForReviewAsync(
        Guid submissionId, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadForReviewAsync(connection, null, request, urls, submissionId) ?? throw NotFound());
    }

    private static async Task<IResult> DecideAsync(
        ClaimsPrincipal user, Guid submissionId, JsonElement body, HttpRequest request, NpgsqlDataSource db, SignedUrls urls,
        CancellationToken cancellationToken)
    {
        var decision = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() : null;
        var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!.Trim() : null;
        var notes = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        var errors = new List<FieldError>();
        if (decision is not ("approve" or "reject"))
        {
            errors.Add(new FieldError("decision", "Use approve or reject."));
        }

        if (decision == "reject" && string.IsNullOrEmpty(reason))
        {
            errors.Add(new FieldError("reason", "Give a short reason the fan will see."));
        }

        if (reason is { Length: > 500 })
        {
            errors.Add(new FieldError("reason", "Use up to 500 characters."));
        }

        if (notes is { Length: > 2000 })
        {
            errors.Add(new FieldError("notes", "Use up to 2000 characters."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var status = await connection.ExecuteScalarAsync<string?>(
            "SELECT status::text FROM submissions WHERE id = @submissionId FOR UPDATE", new { submissionId }, transaction)
            ?? throw NotFound();
        var approve = decision == "approve";
        if ((approve && status == "verified") || (!approve && status == "rejected"))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already decided",
                $"This submission is already {status}.");
        }

        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO verification_checks (submission_id, check_type, result, details, reviewer_id)
                VALUES (@submissionId, 'manual', @result::check_result, @details::jsonb, @reviewerId);
                UPDATE submissions SET status = @status::submission_status, rejection_reason = @reason WHERE id = @submissionId;
                """,
                new
                {
                    submissionId, result = approve ? "pass" : "fail", reviewerId = user.UserId(),
                    details = JsonSerializer.Serialize(new { decision, notes }), status = approve ? "verified" : "rejected",
                    reason = approve ? null : reason,
                },
                transaction);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already verified elsewhere",
                "This ticket or order is already verified on another submission.");
        }

        var result = (await LoadForReviewAsync(connection, transaction, request, urls, submissionId))!;
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(result);
    }

    private static ApiProblemException NotFound() =>
        new(StatusCodes.Status404NotFound, "not_found", "Submission not found", "No submission of yours has this id.");

    private static async Task<Submission> LoadMineAsync(
        NpgsqlConnection connection, Guid userId, HttpRequest request, SignedUrls urls, Guid submissionId)
    {
        var owner = await connection.ExecuteScalarAsync<Guid?>("SELECT user_id FROM submissions WHERE id = @submissionId", new { submissionId });
        if (owner != userId)
        {
            throw NotFound();
        }

        return (await LoadAsync(connection, null, request, urls, submissionId))!;
    }

    private static async Task<Submission?> LoadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, HttpRequest request, SignedUrls urls, Guid submissionId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<SubmissionRow>(
            """
            SELECT s.id AS Id, s.artist_id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl, s.kind::text AS Kind,
                   s.status::text AS Status, s.rejection_reason AS RejectionReason, s.submitted_at AS SubmittedAt,
                   s.verified_at AS VerifiedAt, s.archived_at IS NOT NULL AS Archived, s.caption AS Caption, s.shared_at AS SharedAt,
                   s.user_id AS UserId, s.trust_score AS TrustScore, s.extracted::text AS Extracted,
                   s.removed_at IS NOT NULL AS Removed, s.event_id AS EventId, s.concert_date AS ConcertDate, s.concert_venue AS ConcertVenue, s.concert_city AS ConcertCity
            FROM submissions s JOIN artists a ON a.id = s.artist_id WHERE s.id = @submissionId
            """,
            new { submissionId }, transaction);
        if (row is null)
        {
            return null;
        }

        var files = (await connection.QueryAsync<(Guid Id, string MimeType, bool Redacted)>(
                "SELECT id, mime_type, redacted FROM submission_files WHERE submission_id = @submissionId ORDER BY uploaded_at, id",
                new { submissionId }, transaction))
            .Select(f => new SubmissionFile(f.Id, f.MimeType, UploadEndpoints.FileUrl(request, urls, f.Id), f.Redacted)).ToList();
        var awards = (await connection.QueryAsync<PointsAward>(
            "SELECT rule_key AS RuleKey, points AS Points FROM score_events WHERE source_type = 'submission' AND source_id = @submissionId AND reversed_at IS NULL",
            new { submissionId }, transaction)).ToList();
        var canShare = row.Status == "verified" && ShareableKinds.Contains(row.Kind) && !row.Removed;
        var pinned = row.EventId is { } eventId
            ? await connection.QuerySingleOrDefaultAsync<EventRef>(
                """
                SELECT id AS Id, name AS Name, venue AS Venue, city AS City, starts_at AS StartsAt FROM events WHERE id = @eventId
                """,
                new { eventId }, transaction)
            : null;
        var concert = row.ConcertDate is { } date ? new Concert(date, row.ConcertVenue!, row.ConcertCity!) : null;
        return new Submission(row.Id, row.ArtistId, new ScoringEndpoints.ArtistRef(row.ArtistId, row.ArtistName, row.ArtistImageUrl),
            row.Kind, row.Status, pinned, concert, row.RejectionReason, row.SubmittedAt, row.VerifiedAt, row.Archived, row.Caption,
            row.SharedAt, canShare, row.Removed, files, awards);
    }

    private static async Task<AdminSubmission?> LoadForReviewAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, HttpRequest request, SignedUrls urls, Guid submissionId)
    {
        if (await LoadAsync(connection, transaction, request, urls, submissionId) is not { } s)
        {
            return null;
        }

        var extra = await connection.QuerySingleAsync<(string? Handle, bool Deleted, string? DisplayName, decimal? TrustScore, string Extracted)>(
            """
            SELECT CASE WHEN u.deleted_at IS NULL THEN u.handle::text END, u.deleted_at IS NOT NULL,
                   CASE WHEN u.deleted_at IS NULL THEN u.display_name END, s.trust_score, s.extracted::text
            FROM submissions s JOIN users u ON u.id = s.user_id WHERE s.id = @submissionId
            """,
            new { submissionId }, transaction);
        var checks = (await connection.QueryAsync<CheckRow>(
                """
                SELECT id AS Id, check_type::text AS CheckType, result::text AS Result, confidence AS Confidence,
                       details::text AS Details, reviewer_id AS ReviewerId, created_at AS CreatedAt
                FROM verification_checks WHERE submission_id = @submissionId ORDER BY created_at, id
                """,
                new { submissionId }, transaction))
            .Select(c => new VerificationCheck(c.Id, c.CheckType, c.Result, c.Confidence, Parse(c.Details), c.ReviewerId, c.CreatedAt))
            .ToList();
        return new AdminSubmission(s.Id, s.ArtistId, s.Artist, s.Kind, s.Status, s.Event, s.Concert, s.RejectionReason, s.SubmittedAt,
            s.VerifiedAt, s.Archived, s.Caption, s.SharedAt, s.CanShare, s.Removed, s.Files, s.PointsAwarded,
            new UserRef(extra.Handle, extra.Deleted, extra.DisplayName, null), extra.TrustScore, Parse(extra.Extracted), checks);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record SubmissionRow(
        Guid Id, Guid ArtistId, string ArtistName, string? ArtistImageUrl, string Kind, string Status, string? RejectionReason,
        DateTime SubmittedAt, DateTime? VerifiedAt, bool Archived, string? Caption, DateTime? SharedAt, Guid UserId,
        decimal? TrustScore, string Extracted, bool Removed, Guid? EventId, DateOnly? ConcertDate, string? ConcertVenue, string? ConcertCity);

    private sealed record CheckRow(
        Guid Id, string CheckType, string Result, decimal? Confidence, string Details, Guid? ReviewerId, DateTime CreatedAt);
}
