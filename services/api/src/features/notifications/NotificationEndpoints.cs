using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using FandomScore.Notifications;
using Npgsql;

namespace FandomScore.Api.Features.Notifications;

/// <summary>
/// openapi tag <c>Me</c>: in-app notifications (migration 0021). The database writes them (triggers on the draw,
/// backfill, reports, and proof; the worker for delivered tickets); the API lists them with ready-made text and the
/// app screen they open, and marks them read.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/notifications", ListAsync);
        v1.MapPost("/me/notifications/read", MarkReadAsync);
    }

    /// <summary>openapi Notification. <c>Link</c> is the app route to open (for example <c>/tickets</c>).</summary>
    public sealed record Notification(
        Guid Id, string Kind, string Title, string Body, string Link, DateTime CreatedAt, bool Read, JsonElement Data);

    public sealed record NotificationPage(IReadOnlyList<Notification> Items, int UnreadCount, string? NextCursor);

    public sealed record MarkReadRequest(IReadOnlyList<Guid>? Ids);

    public sealed record ReadResult(int UnreadCount);

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
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

        var me = user.UserId();
        var take = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(
            """
            SELECT id AS Id, kind AS Kind, subject_id AS SubjectId, data::text AS Data, created_at AS CreatedAt, read_at IS NOT NULL AS Read
            FROM notifications WHERE user_id = @me
            ORDER BY created_at DESC, id
            LIMIT @take OFFSET @offset
            """,
            new { me, take = take + 1, offset })).ToList();
        var unread = await CountUnreadAsync(connection, me);
        return Results.Ok(new NotificationPage([.. rows.Take(take).Select(ToNotification)], unread,
            rows.Count > take ? PageCursor.Write(offset + take) : null));
    }

    // Marks the given notifications read (only the caller's), or all of them when no ids are sent. Idempotent.
    private static async Task<IResult> MarkReadAsync(
        ClaimsPrincipal user, MarkReadRequest? request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (request?.Ids is { Count: > 200 })
        {
            throw ApiProblemException.Validation(new FieldError("ids", "Send at most 200 ids, or none to mark everything read."));
        }

        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            UPDATE notifications SET read_at = clock_timestamp()
            WHERE user_id = @me AND read_at IS NULL AND (@all OR id = ANY(@ids))
            """,
            new { me, all = request?.Ids is null || request.Ids.Count == 0, ids = (request?.Ids ?? []).ToArray() });
        return Results.Ok(new ReadResult(await CountUnreadAsync(connection, me)));
    }

    private static Task<int> CountUnreadAsync(NpgsqlConnection connection, Guid me) =>
        connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM notifications WHERE user_id = @me AND read_at IS NULL", new { me });

    private static Notification ToNotification(Row row)
    {
        using var document = JsonDocument.Parse(row.Data);
        var data = document.RootElement.Clone();
        var message = NotificationText.Describe(row.Kind, data, DateTime.UtcNow);
        return new Notification(row.Id, row.Kind, message.Title, message.Body, message.Link, row.CreatedAt, row.Read, data);
    }

    private sealed record Row(Guid Id, string Kind, Guid SubjectId, string Data, DateTime CreatedAt, bool Read);
}
