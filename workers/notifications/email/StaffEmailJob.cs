using System.Text.Json;
using Dapper;
using FandomScore.Notifications;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.Notifications.Email;

/// <summary>
/// <c>notifications.email</c> (payload <c>{"notification_id": ...}</c>): emails a staff notice to the staff member's
/// account address (migration 0059). Queued by the database when a <c>staff_*</c> notification is written for a staff
/// member with "Staff notices by email" on. Does nothing while <c>Email__Enabled</c> is false. Skips a deleted or
/// no-longer-staff account, the setting turned off since, a notice already read, one older than
/// <see cref="EmailOptions.MaxAge"/>, and one already emailed. The text is the in-app text
/// (<see cref="NotificationText"/>) plus a link into the web app. A sender that can't take the request now throws
/// <see cref="EmailUnavailableException"/> before anything is recorded, so the queue retries; a final refusal is
/// recorded as <c>failed</c> and not retried.
/// </summary>
public sealed partial class StaffEmailJob(
    NpgsqlDataSource db, IEmailSender sender, IOptions<EmailOptions> options, TimeProvider time, ILogger<StaffEmailJob> logger) : IJobHandler
{
    public const string JobKind = "notifications.email";

    public const string SubjectPrefix = "Fandom Score: ";

    private readonly EmailOptions _options = options.Value;

    public string Kind => JobKind;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("notification_id", out var idElement) || !idElement.TryGetGuid(out var notificationId))
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT n.kind AS Kind, n.data::text AS Data, n.created_at AS CreatedAt, n.read_at IS NOT NULL AS Read,
                   u.email::text AS Email, u.is_staff AND u.email_staff AS Wanted,
                   EXISTS (SELECT 1 FROM email_deliveries d WHERE d.notification_id = n.id) AS Delivered
            FROM notifications n JOIN users u ON u.id = n.user_id AND u.deleted_at IS NULL
            WHERE n.id = @notificationId
            """,
            new { notificationId });
        var now = time.GetUtcNow().UtcDateTime;
        if (row is null || row.Delivered || row.Read || !row.Wanted || string.IsNullOrEmpty(row.Email)
            || NotificationText.Category(row.Kind) != "staff" || row.CreatedAt < now - _options.MaxAge)
        {
            return;
        }

        using var document = JsonDocument.Parse(row.Data);
        var message = NotificationText.Describe(row.Kind, document.RootElement, now);
        var result = await sender.SendAsync(new EmailMessage(row.Email, SubjectPrefix + message.Title, Text(message, _options)), cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO email_deliveries (notification_id, status, provider_message_id, error)
            VALUES (@notificationId, @status, @messageId, @error)
            ON CONFLICT (notification_id) DO NOTHING
            """,
            new { notificationId, status = result.Ok ? "sent" : "failed", messageId = result.MessageId, error = result.Error });
        if (result.Ok)
        {
            LogSent(logger, notificationId, row.Kind);
        }
        else
        {
            LogFailed(logger, notificationId, row.Kind, result.Error ?? "no reason given");
        }
    }

    /// <summary>The email's text: the in-app sentence, a link to the screen, and why the reader got it.</summary>
    public static string Text(NotificationMessage message, EmailOptions options) =>
        $"""
         {message.Body}

         Open it: {options.AppOriginTrimmed}{message.Link}

         You get these because you're staff on Fandom Score. Turn them off under Profile, "Staff notices by email".
         """;

    private sealed record Row(string Kind, string Data, DateTime CreatedAt, bool Read, string? Email, bool Wanted, bool Delivered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emailed staff notice {NotificationId} ({Kind}).")]
    private static partial void LogSent(ILogger logger, Guid notificationId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email for staff notice {NotificationId} ({Kind}) was refused: {Error}")]
    private static partial void LogFailed(ILogger logger, Guid notificationId, string kind, string error);
}
