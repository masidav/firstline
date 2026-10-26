using System.Text.Json;
using Dapper;
using FandomScore.Notifications;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.Notifications;

/// <summary>
/// <c>notifications.push</c> (payload <c>{"notification_id": ...}</c>): sends a notification to the fan's live devices
/// through Expo (migration 0032). Queued by the database when a notification is written for a fan with a device. Skips
/// a deleted account, a category the fan turned off (<c>push_tickets</c>, <c>push_presales</c>, <c>push_staff</c>), a
/// notification already read or older than <see cref="PushOptions.MaxAge"/>, and devices it already reached. The text is
/// the in-app text (<see cref="NotificationText"/>); the badge is the fan's unread count. A device Expo reports as
/// unregistered is switched off.
/// </summary>
public sealed partial class PushNotificationJob(
    NpgsqlDataSource db, ExpoPushClient expo, IOptions<PushOptions> options, TimeProvider time, ILogger<PushNotificationJob> logger) : IJobHandler
{
    public const string JobKind = "notifications.push";

    private readonly PushOptions _options = options.Value;

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
            SELECT n.user_id AS UserId, n.kind AS Kind, n.data::text AS Data, n.created_at AS CreatedAt, n.read_at IS NOT NULL AS Read,
                   u.push_tickets AS Tickets, u.push_presales AS Presales, u.push_staff AS Staff
            FROM notifications n JOIN users u ON u.id = n.user_id AND u.deleted_at IS NULL
            WHERE n.id = @notificationId
            """,
            new { notificationId });
        var now = time.GetUtcNow().UtcDateTime;
        if (row is null || row.Read || row.CreatedAt < now - _options.MaxAge)
        {
            return;
        }

        var allowed = NotificationText.Category(row.Kind) switch
        {
            "tickets" => row.Tickets,
            "presales" => row.Presales,
            "staff" => row.Staff,
            _ => true,
        };
        if (!allowed)
        {
            return;
        }

        var devices = (await connection.QueryAsync<(Guid Id, string Token)>(
            """
            SELECT d.id, d.token FROM push_devices d
            WHERE d.user_id = @userId AND d.disabled_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM push_deliveries p WHERE p.notification_id = @notificationId AND p.device_id = d.id)
            ORDER BY d.created_at
            """,
            new { userId = row.UserId, notificationId })).ToList();
        if (devices.Count == 0)
        {
            return;
        }

        using var document = JsonDocument.Parse(row.Data);
        var message = NotificationText.Describe(row.Kind, document.RootElement, now);
        var unread = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM notifications WHERE user_id = @userId AND read_at IS NULL", new { userId = row.UserId });
        var data = new Dictionary<string, string> { ["link"] = message.Link, ["notification_id"] = notificationId.ToString() };
        var results = await expo.SendAsync(
            [.. devices.Select(d => new PushMessage(d.Token, message.Title, message.Body, data, unread))], cancellationToken);

        var sent = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            var result = results[i];
            await connection.ExecuteAsync(
                """
                INSERT INTO push_deliveries (notification_id, device_id, status, ticket_id, error)
                VALUES (@notificationId, @deviceId, @status, @ticketId, @error)
                ON CONFLICT (notification_id, device_id) DO NOTHING
                """,
                new { notificationId, deviceId = devices[i].Id, status = result.Ok ? "sent" : "failed", ticketId = result.TicketId, error = result.Error });
            if (result.Ok)
            {
                sent++;
            }
            else if (result.Error == "DeviceNotRegistered")
            {
                await DisableAsync(connection, devices[i].Id, "DeviceNotRegistered");
            }
        }

        LogSent(logger, notificationId, sent, devices.Count);
    }

    internal static Task DisableAsync(NpgsqlConnection connection, Guid deviceId, string reason) =>
        connection.ExecuteAsync(
            "UPDATE push_devices SET disabled_at = now(), disabled_reason = @reason WHERE id = @deviceId AND disabled_at IS NULL",
            new { deviceId, reason });

    private sealed record Row(Guid UserId, string Kind, string Data, DateTime CreatedAt, bool Read, bool Tickets, bool Presales, bool Staff);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pushed notification {NotificationId} to {Sent} of {Devices} devices.")]
    private static partial void LogSent(ILogger logger, Guid notificationId, int sent, int devices);
}

/// <summary>
/// Every 15 minutes, <c>notifications.push_receipts</c>: asks Expo what happened to pushes sent 15 minutes to 23 hours ago
/// (Expo keeps receipts a day), records delivered or failed, and switches off devices Apple or Google no longer know.
/// </summary>
public sealed partial class PushReceiptsJob(NpgsqlDataSource db, ExpoPushClient expo, IOptions<PushOptions> options, ILogger<PushReceiptsJob> logger) : IJobHandler
{
    public string Kind => "notifications.push_receipts";

    public TimeSpan? Every => TimeSpan.FromMinutes(15);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var pending = (await connection.QueryAsync<(Guid NotificationId, Guid DeviceId, string TicketId)>(
            """
            SELECT notification_id, device_id, ticket_id FROM push_deliveries
            WHERE status = 'sent' AND ticket_id IS NOT NULL AND checked_at IS NULL
              AND sent_at < now() - interval '15 minutes' AND sent_at > now() - interval '23 hours'
            ORDER BY sent_at
            LIMIT @max
            """,
            new { max = ExpoPushClient.MaxReceipts })).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var receipts = await expo.GetReceiptsAsync([.. pending.Select(p => p.TicketId)], cancellationToken);
        int delivered = 0, failed = 0;
        foreach (var p in pending)
        {
            if (!receipts.TryGetValue(p.TicketId, out var receipt))
            {
                continue;   // not ready yet; asked again next run
            }

            await connection.ExecuteAsync(
                """
                UPDATE push_deliveries SET status = @status, error = @error, checked_at = now()
                WHERE notification_id = @NotificationId AND device_id = @DeviceId
                """,
                new { p.NotificationId, p.DeviceId, status = receipt.Ok ? "delivered" : "failed", error = receipt.Error });
            if (receipt.Ok)
            {
                delivered++;
            }
            else
            {
                failed++;
                if (receipt.Error == "DeviceNotRegistered")
                {
                    await PushNotificationJob.DisableAsync(connection, p.DeviceId, "DeviceNotRegistered");
                }
            }
        }

        LogChecked(logger, delivered, failed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push receipts: {Delivered} delivered, {Failed} failed.")]
    private static partial void LogChecked(ILogger logger, int delivered, int failed);
}
