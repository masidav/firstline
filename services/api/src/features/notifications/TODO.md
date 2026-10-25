# TODO — Notifications API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define supported notification types, triggers, preferences, and delivery channels.
- [ ] Store device push tokens securely and associate them with the correct user/device.
- [ ] Implement register, refresh, and revoke device-token operations.
- [ ] Honor opt-in preferences and platform notification permissions.
- [ ] Queue notifications in background jobs and prevent duplicate sends.
- [ ] Support score changes, relevant artist/tour news, and friend activity only when those features and data sources exist.
- [ ] Handle invalid device tokens and provider delivery failures.
- [ ] Add tests for preferences, deduplication, revocation, and failure handling.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.

## In-app notifications (2026-10-25)
- [x] `notifications` table (migration 0021), written by triggers (draw, backfill, reports, proof) and the worker
      (`notify_delivered_tickets()`, delivered tickets), once per person, kind, and subject; deleted with the account.
      `GET /me/notifications` (title, body, and app link made by the API), `POST /me/notifications/read`.
      Verified: NotificationApiTests, invariants (0021 section).

## Staff email (added 2026-10-24)
- [ ] Email staff when a post is reported (first open report on a post, not every report).
- [ ] Email staff when proof is submitted and waits in the review queue.
- [ ] Email staff when allocation selection runs for a show (who was selected, counts).
- [ ] Choose the provider and a staff address list; send from the worker (job queue), never inline in a request.
