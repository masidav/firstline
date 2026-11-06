# Notifications API

In-app notifications (migration 0021, 2026-10-25), also pushed to the fan's phones (migration 0032, 2026-10-26), and
staff notices also emailed to staff (migration 0059, 2026-11-06; off until a provider is configured). The text comes from `packages/notifications` (`NotificationText`), shared
with the worker so a push reads like its in-app row.

## What is sent
| Kind | Who | Written by |
| --- | --- | --- |
| `draw_selected`, `draw_not_selected` | each live registration's fan | trigger on the draw's outcome |
| `seat_backfilled` | the fan a returned seat went to | same trigger (not selected → selected) |
| `ticket_delivered` | the reservation's holder | worker `notifications.tickets_delivered` (every 15 minutes), `notify_delivered_tickets()` |
| `staff_draw` | every live staff account | trigger on `allocation_selections` |
| `staff_report` | staff | trigger on `reports`, first open report on a post only |
| `staff_review` | staff | trigger on `submissions` reaching `needs_review` |
| `staff_redaction` | staff | trigger on a submission file reaching `needs_review` (migration 0029) |
| `staff_show_changed` | staff | trigger on `events` when Ticketmaster moves, cancels, postpones, or reschedules a show with a live allocation (migration 0036) |
| `presale` | fans with a score for the artist, in the show's hub city | worker `notifications.presales`, `notify_presales()` (migration 0031) |

One row per person, kind, and subject (`notifications_once`), so retries never notify twice. Only `read_at` changes
afterwards (`trg_notifications_guard`). A deleted account's notifications are deleted and it gets no new ones.

## Endpoints
- `GET /v1/me/notifications`: newest first with `unread_count`; each item has ready-made `title` and `body`, the app
  route to open (`link`), and the `data` it was made from.
- `POST /v1/me/notifications/read`: the given `ids` (the caller's only), or all when none are sent.
- `PUT /v1/me/push-devices` (`PushDeviceEndpoints.cs`): the phone app registers its Expo push token on each launch once
  notifications are allowed; a token moves to whoever signed in last on that phone. `DELETE /v1/me/push-devices/{id}`
  on sign-out.
- Push settings are on the profile: `Me.push` (`tickets`, `presales`, `staff`, all on by default), changed with
  `PATCH /v1/me` `{"push": {"presales": false}}`. Email has its own: `Me.email_settings` (`staff`, on by default),
  changed with `{"email_settings": {"staff": false}}`; it only ever applies to staff accounts.

## Push
Every new notification for a fan with a live device queues `notifications.push` (trigger, migration 0032). The worker
(`workers/notifications/PushNotificationJob.cs`) sends it through Expo unless the category is off, the notification was
already read, or it is older than `Push__MaxAge` (12 hours); the badge is the unread count; tapping opens `link`.
`notifications.push_receipts` (every 15 minutes) records delivered or failed and switches off devices Apple or Google
no longer know (`DeviceNotRegistered`). A deleted account's devices are deleted at once.

## Email (staff only)
Every new `staff_*` notification for a staff member with `email_staff` on queues `notifications.email` (trigger,
migration 0059). The worker (`workers/notifications/email/StaffEmailJob.cs`) sends the same title and sentence to the
account's email address with a link into the web app (`Email__AppOrigin` + `link`), unless email is off for the
deployment (`Email__Enabled`, false by default: no provider is chosen yet), the setting was turned off since, the
notice was read, it is older than `Email__MaxAge` (12 hours), or it was emailed already. One `email_deliveries` row
per notification (`sent` or `failed` with the sender's reason), pruned after 30 days. Fans get no email from this: no
fan kind queues one, and the job refuses anything but a staff kind. Senders: `workers/notifications/README.md`.

Tests: `tests/FandomScore.Tests/NotificationApiTests.cs`, `StaffEmailTests.cs`, `database/tests/invariants.sql`
(0021 and 0059 sections).
