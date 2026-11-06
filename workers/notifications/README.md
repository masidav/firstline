# Notification worker jobs

| File | Job | What |
| --- | --- | --- |
| `TicketDeliveredJob.cs` | `notifications.tickets_delivered` (every 15 minutes) | Tells holders their ticket has arrived once a reservation's `deliver_at` passes (`notify_delivered_tickets()`, migration 0021). Each reservation once; returned, cancelled, and started shows are skipped. |
| `PushNotificationJob.cs` | `notifications.push` (queued per new notification for a fan with a device) and `notifications.push_receipts` (every 15 minutes) | Sends a notification to the fan's live devices through Expo (`ExpoPushClient`), honoring `push_tickets`, `push_presales`, `push_staff`; skips read and stale (`Push__MaxAge`, 12 h) ones; badge = unread count. Records each send in `push_deliveries`, then Expo's receipt; `DeviceNotRegistered` switches the device off. `Push__Enabled=false` stops all pushes; `Push__ExpoAccessToken` only if the Expo project has enhanced push security. |
| `PresaleReminderJob.cs` | `notifications.presales` (every 15 minutes) | Reminds fans of a presale opening within a day (`notify_presales()`, migration 0031): shows by artists the fan has a score for, in one of their hub cities; not cancelled, not started, presale not ended. Once per fan per presale (the id comes from the show, presale name, and start, so syncs that replace presale rows don't repeat it). Fans turn it off with `presale_reminders` (`PATCH /v1/me`, the Profile switch). The text says when relative to now ("starts in about 5 hours"); the show screen has exact times. |
| `email/StaffEmailJob.cs` | `notifications.email` (queued per new `staff_*` notification for a staff member with `email_staff` on, migration 0059) | Emails the staff notice (same title and sentence as the in-app row, plus a link into the web app) to the account's email address through the configured `IEmailSender`. Does nothing while `Email__Enabled` is false. Skips a notice already read, older than `Email__MaxAge` (12 h), already emailed, or whose account is gone, no longer staff, or turned the setting off. Records each send in `email_deliveries` (`sent`, or `failed` with the sender's reason; pruned after 30 days). An `EmailUnavailableException` from the sender leaves no record, so the queue retries. |

Everything else in `services/api/src/features/notifications/README.md` is written by database triggers.

## Email (`Email` section)
The provider is still an open decision (PROJECT_TODO, "Choose an email provider for staff notifications"), so the job
is built against `IEmailSender` (`email/IEmailSender.cs`) and the only sender today writes the email to the log.
Choosing a provider means one class implementing `IEmailSender` (an SMTP relay or the provider's HTTP API; a
transient failure throws `EmailUnavailableException`, a final refusal returns `EmailResult(false, Error: ...)`), a name
for it in `EmailSetup.AddEmail`, its secret in Secret Manager and `secrets.yaml` like `Ticketmaster__ApiKey`, and the
settings below in the overlay.

| Setting | Default | What |
| --- | --- | --- |
| `Email__Enabled` | `false` | Nothing is sent until true. The queue trigger still runs, so turning it on later emails only new notices (older ones are past `MaxAge`). |
| `Email__Provider` | `log` | Which sender. `log` writes the whole email (address included) to the worker's log at Information: for development only, since GCP keeps logs 30 days. |
| `Email__From` | | The From address, e.g. `Fandom Score <no-reply@example.com>`. Required when enabled. |
| `Email__AppOrigin` | | The fan web app's origin for the link in each email (`https://app.beta.firstline-5730kl.com`). Required when enabled; no path. |
| `Email__MaxAge` | `12:00:00` | A notice still unsent this long after it was written is not emailed. |

Bad settings stop the worker at startup (`EmailOptions.Problem()`), like the API's `App__PublicOrigin`.
