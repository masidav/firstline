# TODO — Moderation

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define reportable content and report reasons for comments, reviews, users, and submissions as appropriate.
- [ ] Implement authenticated reporting and duplicate/spam report controls.
- [ ] Create staff-only queues and actions with authorization checks.
- [ ] Support documented hide/remove/restore or other moderation outcomes.
- [ ] Implement user blocking if included in the approved product scope.
- [ ] Record moderation actions and reasons in an audit trail.
- [ ] Define artist takedown handling and paused-artist behavior.
- [ ] Add tests for permissions, report lifecycle, audit history, and abuse cases.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.

## Status (2026-10-24, branch `reports`)
Built: reports on shared photos and public album logs (`POST /reports`, one open report per fan and post, 30 a day),
the staff queue grouped by post (`GET /admin/reports`), take down or dismiss (`POST /admin/reports/{type}/{id}/decision`),
and the append-only audit trail (`moderation_actions`). Takedown: photos leave the feed for good (proof and points
stay); logs are hidden from everyone but their author and lose their points. Tests: ReportApiTests, invariants.
Not yet: restoring a taken-down post, reporting comments, lists, or users, blocking (not wanted for now), staff email.

## Account pauses (2026-10-24, branch `reports`)
Built: staff pause an account (`bot` or `other`, 1–365 days) and lift it early; a paused fan can't register for
allocations (`registration_blocker()` returns `suspended`) or post (uploads, submissions, sharing, album logs), gets
`403 account_suspended`, sees the pause on `GET /me`, and can ask once per pause to be unbanned; staff lift or deny
with a reply. Audit in `moderation_actions` (`account` target). Tests: SuspensionApiTests, invariants.
Not yet: emailing staff about new requests, hiding a paused fan's existing posts, an automatic bot detector (Phase 10).
