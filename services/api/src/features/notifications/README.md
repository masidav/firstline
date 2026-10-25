# Notifications API

In-app notifications (migration 0021, 2026-10-25). Push and email are later channels over the same rows.

## What is sent
| Kind | Who | Written by |
| --- | --- | --- |
| `draw_selected`, `draw_not_selected` | each live registration's fan | trigger on the draw's outcome |
| `seat_backfilled` | the fan a returned seat went to | same trigger (not selected → selected) |
| `ticket_delivered` | the reservation's holder | worker `notifications.tickets_delivered` (every 15 minutes), `notify_delivered_tickets()` |
| `staff_draw` | every live staff account | trigger on `allocation_selections` |
| `staff_report` | staff | trigger on `reports`, first open report on a post only |
| `staff_review` | staff | trigger on `submissions` reaching `needs_review` |

One row per person, kind, and subject (`notifications_once`), so retries never notify twice. Only `read_at` changes
afterwards (`trg_notifications_guard`). A deleted account's notifications are deleted and it gets no new ones.

## Endpoints
- `GET /v1/me/notifications`: newest first with `unread_count`; each item has ready-made `title` and `body`, the app
  route to open (`link`), and the `data` it was made from.
- `POST /v1/me/notifications/read`: the given `ids` (the caller's only), or all when none are sent.

Tests: `tests/FandomScore.Tests/NotificationApiTests.cs`, `database/tests/invariants.sql` (0021 section).
