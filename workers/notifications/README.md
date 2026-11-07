# Notification worker jobs

| File | Job | What |
| --- | --- | --- |
| `TicketDeliveredJob.cs` | `notifications.tickets_delivered` (every 15 minutes) | Tells holders their ticket has arrived once a reservation's `deliver_at` passes (`notify_delivered_tickets()`, migration 0021). Each reservation once; returned, cancelled, and started shows are skipped. |
| `PushNotificationJob.cs` | `notifications.push` (queued per new notification for a fan with a device) and `notifications.push_receipts` (every 15 minutes) | Sends a notification to the fan's live devices through Expo (`ExpoPushClient`), honoring `push_tickets`, `push_presales`, `push_staff`; skips read and stale (`Push__MaxAge`, 12 h) ones; badge = unread count. Records each send in `push_deliveries`, then Expo's receipt; `DeviceNotRegistered` switches the device off. `Push__Enabled=false` stops all pushes; `Push__ExpoAccessToken` only if the Expo project has enhanced push security. |
| `PresaleReminderJob.cs` | `notifications.presales` (every 15 minutes) | Reminds fans of a presale opening within a day (`notify_presales()`, migration 0031): shows by artists the fan has a score for, in one of their hub cities; not cancelled, not started, presale not ended. Once per fan per presale (the id comes from the show, presale name, and start, so syncs that replace presale rows don't repeat it). Fans turn it off with `presale_reminders` (`PATCH /v1/me`, the Profile switch). The text says when relative to now ("starts in about 5 hours"); the show screen has exact times. |
| `email/StaffEmailJob.cs` | `notifications.email` (queued per new `staff_*` notification for a staff member with `email_staff` on, migration 0059) | Emails the staff notice (same title and sentence as the in-app row, plus a link into the web app) to the account's email address through the configured `IEmailSender`. Does nothing while `Email__Enabled` is false. Skips a notice already read, older than `Email__MaxAge` (12 h), already emailed, or whose account is gone, no longer staff, or turned the setting off. Records each send in `email_deliveries` (`sent`, or `failed` with the sender's reason; pruned after 30 days). An `EmailUnavailableException` from the sender leaves no record, so the queue retries. |

Everything else in `services/api/src/features/notifications/README.md` is written by database triggers.

## Email (`Email` section)
The job is built against `IEmailSender` (`email/IEmailSender.cs`); a transient failure throws
`EmailUnavailableException` (the queue retries), a final refusal returns `EmailResult(false, Error: ...)` (recorded,
not retried). Two senders exist, picked by `Email__Provider`:
- `log` writes the email to the worker's log (development).
- `smtp` (`SmtpEmailSender`, MailKit) sends through an SMTP relay with a login, one connection per message. Decided
  2026-11-07: everything is sent as the owner's account `d57590971@gmail.com` through Gmail's relay
  (`smtp.gmail.com`, STARTTLS on 587), so there is no domain to verify; the password is a Google app password stored
  as the secret `Email__Smtp__Password` (Terraform creates the secret; the owner adds the version;
  `docs/OWNER_TODO.md`, section B). Gmail sends as the signed-in account whatever the From header says, and a free
  account sends about 500 messages a day, enough for staff notices; fan email later will need a Workspace account or
  another relay (then only these settings change). A 5xx on the sender, recipient, or message is final; a refused
  login, a 4xx, or an unreachable relay is retried, so a fixed password lets queued notices go out.
  `tests/FandomScore.Tests/SmtpEmailSenderTests.cs` runs it against an in-process relay.

| Setting | Default | What |
| --- | --- | --- |
| `Email__Enabled` | `false` | Nothing is sent until true. The queue trigger still runs, so turning it on later emails only new notices (older ones are past `MaxAge`). |
| `Email__Provider` | `log` | Which sender: `log` writes the whole email (address included) to the worker's log at Information (development only, since GCP keeps logs 30 days); `smtp` sends through the relay below. |
| `Email__From` | | The From address, e.g. `Fandom Score <no-reply@example.com>`. Required when enabled. |
| `Email__AppOrigin` | | The fan web app's origin for the link in each email (`https://app.beta.firstline-5730kl.com`). Required when enabled; no path. |
| `Email__MaxAge` | `12:00:00` | A notice still unsent this long after it was written is not emailed. |
| `Email__Smtp__Host` / `Email__Smtp__Port` | `smtp.gmail.com` / `587` | The relay (`smtp` only). |
| `Email__Smtp__Security` | `starttls` | `starttls` (587), `ssl` (465), or `none` (local test relays). |
| `Email__Smtp__Username` | the From address | The login. |
| `Email__Smtp__Password` | | Secret. Whitespace counts as unset; the worker refuses to start with `smtp` enabled and no password. |

Bad settings stop the worker at startup (`EmailOptions.Problem()`), like the API's `App__PublicOrigin`.
