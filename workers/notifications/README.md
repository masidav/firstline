# Notification worker jobs

| File | Job | What |
| --- | --- | --- |
| `TicketDeliveredJob.cs` | `notifications.tickets_delivered` (every 15 minutes) | Tells holders their ticket has arrived once a reservation's `deliver_at` passes (`notify_delivered_tickets()`, migration 0021). Each reservation once; returned, cancelled, and started shows are skipped. |
| `PresaleReminderJob.cs` | `notifications.presales` (every 15 minutes) | Reminds fans of a presale opening within a day (`notify_presales()`, migration 0031): shows by artists the fan has a score for, in one of their hub cities; not cancelled, not started, presale not ended. Once per fan per presale (the id comes from the show, presale name, and start, so syncs that replace presale rows don't repeat it). Fans turn it off with `presale_reminders` (`PATCH /v1/me`, the Profile switch). The text says when relative to now ("starts in about 5 hours"); the show screen has exact times. |

Everything else in `services/api/src/features/notifications/README.md` is written by database triggers. Push and email
senders will live here once their providers are chosen.
