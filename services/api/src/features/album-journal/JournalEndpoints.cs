using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Moderation;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.AlbumJournal;

/// <summary>
/// openapi tag Journal: logging albums, reading and editing logs, and diaries. Points come from the database
/// triggers on <c>album_logs</c> (first log, relisten, 100+ character review); the API only reports what a write
/// earned. Whether a log is a relisten is decided here, never by the client.
/// </summary>
public static class JournalEndpoints
{
    public const int ReviewMaxLength = 10000;
    private static readonly DateOnly EarliestListen = new(1900, 1, 1);

    public static void MapJournal(this RouteGroupBuilder v1)
    {
        v1.MapPost("/logs", CreateAsync);
        v1.MapGet("/logs/{logId:guid}", GetAsync);
        v1.MapPatch("/logs/{logId:guid}", UpdateAsync);
        v1.MapDelete("/logs/{logId:guid}", DeleteAsync);
        v1.MapGet("/me/logs", ListMineAsync);
        v1.MapGet("/users/{handle}/logs", ListForUserAsync);
    }

    /// <summary>Fields of LogCreate / LogUpdate, with "sent" flags so an update leaves missing fields alone.</summary>
    private sealed class LogFields
    {
        public Guid? AlbumId { get; set; }
        public bool RatingSent { get; set; }
        public int? Rating { get; set; }
        public bool ReviewSent { get; set; }
        public string? Review { get; set; }
        public DateOnly? ListenedOn { get; set; }
        public string? Visibility { get; set; }
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user, HttpRequest request, JsonElement body, NpgsqlDataSource db, TimeProvider time,
        IOptions<JsonOptions> json, CancellationToken cancellationToken)
    {
        var key = Idempotency.ReadKey(request);
        var fields = Parse(body, time, creating: true);
        var userId = user.UserId();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await Suspensions.EnsureNotSuspendedAsync(connection, userId);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (key is not null
            && await Idempotency.BeginAsync(connection, transaction, userId, key, Idempotency.Hash(body.GetRawText())) is { } replay)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay.ToResult();
        }

        // One log at a time per user, so two first logs of an album can't both miss the relisten check.
        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('album_log:' || @userId::text, 0))",
            new { userId }, transaction);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM albums WHERE id = @albumId AND delisted_at IS NULL)", new { fields.AlbumId }, transaction))
        {
            throw Journal.NotFound("Album");
        }

        var logId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO album_logs (user_id, album_id, rating, review, listened_on, is_relisten, visibility)
            VALUES (@userId, @albumId, @rating, @review, @listenedOn,
                    EXISTS (SELECT 1 FROM album_logs WHERE user_id = @userId AND album_id = @albumId AND deleted_at IS NULL),
                    @visibility::visibility)
            RETURNING id
            """,
            new
            {
                userId, fields.AlbumId, fields.Rating, fields.Review,
                listenedOn = fields.ListenedOn ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
                visibility = fields.Visibility ?? "public",
            },
            transaction);

        var result = new LogResult(await LoadAsync(connection, transaction, logId, userId),
            await Journal.AwardsAsync(connection, transaction, "album_log", logId, afterId: 0));
        if (key is not null)
        {
            await Idempotency.CompleteAsync(connection, transaction, userId, key, StatusCodes.Status201Created,
                JsonSerializer.Serialize(result, json.Value.SerializerOptions));
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/v1/logs/{logId}", result);
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Guid logId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Journal.LogRow>(
            $"{Journal.LogSelect} WHERE l.id = @logId AND l.deleted_at IS NULL AND {Journal.VisibleToViewer}",
            new { logId, viewer = user.UserId() })
            ?? throw Journal.NotFound("Log");
        return Results.Ok(Journal.ToLog(row));
    }

    // A missing field is left alone; rating and review accept null to clear. Adding a 100+ character review
    // earns review_written (once); shortening it reverses that award (trigger album_logs_change).
    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal user, Guid logId, JsonElement body, NpgsqlDataSource db, TimeProvider time, CancellationToken cancellationToken)
    {
        var fields = Parse(body, time, creating: false);
        var userId = user.UserId();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await Suspensions.EnsureNotSuspendedAsync(connection, userId);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, logId, userId, "Only the log's owner can edit it.");

        var sets = new List<string>();
        var parameters = new DynamicParameters(new { logId });
        if (fields.RatingSent)
        {
            sets.Add("rating = @rating");
            parameters.Add("rating", fields.Rating);
        }

        if (fields.ReviewSent)
        {
            sets.Add("review = @review");
            parameters.Add("review", fields.Review);
        }

        if (fields.ListenedOn is { } listenedOn)
        {
            sets.Add("listened_on = @listenedOn");
            parameters.Add("listenedOn", listenedOn);
        }

        if (fields.Visibility is { } visibility)
        {
            sets.Add("visibility = @visibility::visibility");
            parameters.Add("visibility", visibility);
        }

        var before = await Journal.LastAwardIdAsync(connection, transaction);
        if (sets.Count > 0)
        {
            await connection.ExecuteAsync($"UPDATE album_logs SET {string.Join(", ", sets)} WHERE id = @logId", parameters, transaction);
        }

        var result = new LogResult(await LoadAsync(connection, transaction, logId, userId),
            await Journal.AwardsAsync(connection, transaction, "album_log", logId, before));
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(result);
    }

    // Soft delete: the row stays (comments and reversed ledger rows point at it) but leaves every listing.
    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, Guid logId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, logId, user.UserId(), "Only the log's owner can delete it.");
        await connection.ExecuteAsync("UPDATE album_logs SET deleted_at = now() WHERE id = @logId AND deleted_at IS NULL",
            new { logId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal user, Guid? artistId, int? year, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        if (year is < 1900 or > 2100)
        {
            errors.Add(new FieldError("year", "Use a year from 1900 to 2100."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Journal.LogRow>(
            $"""
             {Journal.LogSelect}
             WHERE l.user_id = @viewer AND l.deleted_at IS NULL
               AND (@artistId::uuid IS NULL OR a.artist_id = @artistId::uuid)
               AND (@year::int IS NULL OR extract(year FROM l.listened_on) = @year::int)
             ORDER BY l.listened_on DESC, l.created_at DESC, l.id
             LIMIT @take OFFSET @offset
             """,
            new { viewer = user.UserId(), artistId, year, take = take + 1, offset })).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new LogPage([.. items.Select(Journal.ToLog)], next));
    }

    // openapi /users/{handle}/logs (tag Social): another fan's logs the caller may see.
    private static async Task<IResult> ListForUserAsync(
        ClaimsPrincipal user, string handle, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ownerId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE handle = @handle AND deleted_at IS NULL", new { handle })
            ?? throw Journal.NotFound("User");
        var rows = (await connection.QueryAsync<Journal.LogRow>(
            $"""
             {Journal.LogSelect}
             WHERE l.user_id = @ownerId AND l.deleted_at IS NULL AND {Journal.VisibleToViewer}
             ORDER BY l.listened_on DESC, l.created_at DESC, l.id
             LIMIT @take OFFSET @offset
             """,
            new { ownerId, viewer = user.UserId(), take = take + 1, offset })).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new LogPage([.. items.Select(Journal.ToLog)], next));
    }

    private static async Task<AlbumLog> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid logId, Guid viewer)
    {
        var row = await connection.QuerySingleAsync<Journal.LogRow>(
            $"{Journal.LogSelect} WHERE l.id = @logId", new { logId, viewer }, transaction);
        return Journal.ToLog(row);
    }

    // 404 when the log is gone or not visible to the caller, 403 when visible but someone else's.
    private static async Task RequireOwnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid logId, Guid userId, string forbiddenDetail)
    {
        var owner = await connection.QuerySingleOrDefaultAsync<(Guid UserId, string Visibility)?>(
            "SELECT user_id, visibility::text FROM album_logs WHERE id = @logId AND deleted_at IS NULL FOR UPDATE",
            new { logId }, transaction);
        if (owner is null || (owner.Value.UserId != userId && owner.Value.Visibility != "public"))
        {
            throw Journal.NotFound("Log");
        }

        if (owner.Value.UserId != userId)
        {
            throw Journal.Forbidden(forbiddenDetail);
        }
    }

    private static LogFields Parse(JsonElement body, TimeProvider time, bool creating)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var fields = new LogFields();
        var errors = new List<FieldError>();

        if (body.TryGetProperty("album_id", out var albumId) && albumId.ValueKind == JsonValueKind.String
            && Guid.TryParse(albumId.GetString(), out var albumGuid))
        {
            fields.AlbumId = albumGuid;
        }
        else if (creating)
        {
            errors.Add(new FieldError("album_id", "Send the album's id."));
        }

        if (body.TryGetProperty("rating", out var rating))
        {
            fields.RatingSent = true;
            if (rating.ValueKind == JsonValueKind.Number && rating.TryGetInt32(out var value) && value is >= 1 and <= 10)
            {
                fields.Rating = value;
            }
            else if (rating.ValueKind != JsonValueKind.Null)
            {
                errors.Add(new FieldError("rating", "Use a whole number from 1 (half a star) to 10 (five stars), or null."));
            }
        }

        if (body.TryGetProperty("review", out var review))
        {
            fields.ReviewSent = true;
            if (review.ValueKind == JsonValueKind.String)
            {
                var text = review.GetString()!.Trim();
                if (text.EnumerateRunes().Count() > ReviewMaxLength)
                {
                    errors.Add(new FieldError("review", $"Use at most {ReviewMaxLength} characters."));
                }

                fields.Review = text.Length == 0 ? null : text;
            }
            else if (review.ValueKind != JsonValueKind.Null)
            {
                errors.Add(new FieldError("review", "Send text, or null to remove the review."));
            }
        }

        if (body.TryGetProperty("listened_on", out var listenedOn))
        {
            // Up to a day ahead of UTC covers every local "today".
            var latest = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1);
            if (listenedOn.ValueKind == JsonValueKind.String && Journal.TryParseDate(listenedOn.GetString()!, out var date)
                && date >= EarliestListen && date <= latest)
            {
                fields.ListenedOn = date;
            }
            else
            {
                errors.Add(new FieldError("listened_on", $"Use a date (YYYY-MM-DD) between {EarliestListen:yyyy-MM-dd} and today."));
            }
        }

        if (body.TryGetProperty("visibility", out var visibility))
        {
            if (visibility.ValueKind == JsonValueKind.String && Journal.Visibilities.Contains(visibility.GetString()!, StringComparer.Ordinal))
            {
                fields.Visibility = visibility.GetString();
            }
            else
            {
                errors.Add(new FieldError("visibility", "Use public, followers, or private."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        return fields;
    }
}
