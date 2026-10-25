# Notification worker jobs

| File | Job | What |
| --- | --- | --- |
| `TicketDeliveredJob.cs` | `notifications.tickets_delivered` (every 15 minutes) | Tells holders their ticket has arrived once a reservation's `deliver_at` passes (`notify_delivered_tickets()`, migration 0021). Each reservation once; returned, cancelled, and started shows are skipped. |

Everything else in `services/api/src/features/notifications/README.md` is written by database triggers. Push and email
senders will live here once their providers are chosen.
