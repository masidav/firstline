using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
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
        string Text(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        int Number(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        var show = Text("event_name") is { Length: > 0 } name ? name : "the show";
        var when = data.TryGetProperty("starts_at", out var startsAt) && startsAt.ValueKind == JsonValueKind.String
                   && DateTime.TryParse(startsAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var start)
            ? start.ToString("MMM d", CultureInfo.InvariantCulture)
            : null;
        var on = when is null ? "" : $" on {when}";
        var seats = Number("seats") == 2 ? "seats for you and your +1" : "a seat";
        var (title, body, link) = row.Kind switch
        {
            "draw_selected" => ("You got tickets!", $"The fan draw for {show}{on} picked you: {seats} at face value. Your ticket arrives shortly before the show.", "/tickets"),
            "draw_not_selected" => ("Not this time", $"The fan draw for {show} didn't pick you. If a selected fan returns their seat, it goes to the next in line.", $"/show/{Text("event_id")}"),
            "seat_backfilled" => ("A seat opened up for you", $"A fan returned their seat for {show}{on}, and it's yours: {seats} at face value.", "/tickets"),
            "ticket_delivered" => ("Your ticket is here", $"Your ticket for {show}{on} has been delivered. Open Tickets to show it at the door.", "/tickets"),
            "presale" => (Text("artist_name") is { Length: > 0 } artist ? $"Presale: {artist}" : "Presale",
                $"{Text("presale_name")} for {show}{(Text("city") is { Length: > 0 } city ? $" in {city}" : "")} {PresaleWhen(data, DateTime.UtcNow)}.",
                $"/show/{Text("event_id")}"),
            "staff_report" => ("A post was reported", $"A {(Text("target_type") == "photo" ? "photo" : "album log")} was reported ({Text("reason")}).", "/moderation"),
            "staff_review" => ("Proof to review", $"@{Text("handle")} sent {Text("kind").Replace('_', ' ')} for {Text("artist_name")}.", "/review"),
            "staff_redaction" => ("A photo needs a redaction check", Text("note") is { Length: > 0 } note ? note : "The redaction worker couldn't finish a file.", "/redactions"),
            "staff_draw" => ("A fan draw ran", $"{show}: {Number("selected_fans")} of {Number("registrations")} fans selected, {Number("seats_assigned")} of {Number("tickets")} seats assigned.", $"/show/{Text("event_id")}"),
            _ => ("Update", "Something changed.", "/home"),
        };
        return new Notification(row.Id, row.Kind, title, body, link, row.CreatedAt, row.Read, data);
    }

    /// <summary>
    /// When a presale opens, relative to now ("starts in about 5 hours", "is on now"): the API doesn't know the fan's time
    /// zone, and the show screen has the exact times.
    /// </summary>
    public static string PresaleWhen(JsonElement data, DateTime now)
    {
        static DateTime? Time(JsonElement data, string name) =>
            data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var time)
                ? time
                : null;

        if (Time(data, "ends_at") is { } ends && ends <= now)
        {
            return "has ended";
        }

        if (Time(data, "starts_at") is not { } starts || starts <= now)
        {
            return "is on now";
        }

        var hours = (starts - now).TotalHours;
        return hours switch
        {
            < 1 => "starts within the hour",
            < 1.5 => "starts in about an hour",
            < 36 => $"starts in about {Math.Round(hours)} hours",
            _ => $"starts in {Math.Round(hours / 24)} days",
        };
    }

    private sealed record Row(Guid Id, string Kind, Guid SubjectId, string Data, DateTime CreatedAt, bool Read);
}
