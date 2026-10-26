# Notification worker jobs

| File | Job | What |
| --- | --- | --- |
| `TicketDeliveredJob.cs` | `notifications.tickets_delivered` (every 15 minutes) | Tells holders their ticket has arrived once a reservation's `deliver_at` passes (`notify_delivered_tickets()`, migration 0021). Each reservation once; returned, cancelled, and started shows are skipped. |
| `PushNotificationJob.cs` | `notifications.push` (queued per new notification for a fan with a device) and `notifications.push_receipts` (every 15 minutes) | Sends a notification to the fan's live devices through Expo (`ExpoPushClient`), honoring `push_tickets`, `push_presales`, `push_staff`; skips read and stale (`Push__MaxAge`, 12 h) ones; badge = unread count. Records each send in `push_deliveries`, then Expo's receipt; `DeviceNotRegistered` switches the device off. `Push__Enabled=false` stops all pushes; `Push__ExpoAccessToken` only if the Expo project has enhanced push security. |
| `PresaleReminderJob.cs` | `notifications.presales` (every 15 minutes) | Reminds fans of a presale opening within a day (`notify_presales()`, migration 0031): shows by artists the fan has a score for, in one of their hub cities; not cancelled, not started, presale not ended. Once per fan per presale (the id comes from the show, presale name, and start, so syncs that replace presale rows don't repeat it). Fans turn it off with `presale_reminders` (`PATCH /v1/me`, the Profile switch). The text says when relative to now ("starts in about 5 hours"); the show screen has exact times. |

Everything else in `services/api/src/features/notifications/README.md` is written by database triggers. Email senders will
live here once a provider is chosen.
