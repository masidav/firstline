# Fraud review, appeals, and the eligibility audit

Migration `0042_fraud_review.sql`, endpoints in `FraudEndpoints.cs`. Tests: `FraudReviewApiTests`, invariants (0042).

## What it does
- **Flags.** Staff flag a fan (`POST /v1/admin/fraud-flags`) with a reason (`streaming_farm`, `bot`, `shared_device`,
  `duplicate_account`, `other`), an internal note, and optional evidence (JSON). One open flag per fan.
  `source = 'rule'` is reserved for automatic detection (not built yet).
- **The hold.** While a flag is `open` or `upheld` (`fan_on_hold()`), the fan:
  - can't register (`registration_blocker()` answers `under_review`; the API returns `403 under_review`);
  - is withdrawn from any allocation the draw runs (`withdrawn_reason = 'fraud_hold'`, before the trust snapshots and
    ranking, so the draw stays reproducible from stored data);
  - is skipped when a returned seat is handed on (`backfill_allocation()`).

  A registration made before the flag stays live until the draw, so clearing the flag in time restores it. Tickets
  already given out are **not** taken back (open decision).
- **Decisions.** `POST /v1/admin/fraud-flags/{id}/decision`: `uphold` an open flag, or `clear` an open or upheld one.
  A cleared flag is final; a new flag can be raised later.
- **Appeals.** The fan sees their flags at `GET /v1/me/fraud-review` (reason and status only) and appeals once per flag
  while it holds them (`POST /v1/me/fraud-review/flags/{id}/appeal`). Staff `grant` (clears the flag) or `deny`
  (upholds it) with a response the fan sees (`POST /v1/admin/fraud-appeals/{id}/decision`, `decide_fraud_appeal()`).
- **Notices.** Fan: `fraud_hold`, `fraud_cleared` (when staff clear without an appeal), `appeal_decided`. Staff:
  `staff_appeal`.
- **Audit.** `eligibility_audit` is append-only (triggers refuse update, delete, truncate). It records every
  registration with its score snapshot and threshold, each refused registration (at most once an hour per fan,
  allocation, and reason, via `log_registration_blocked()`), `draw_held_out`, `backfill_skipped`, and every flag and
  appeal change with the actor (null for the system). Staff read it at `GET /v1/admin/eligibility-audit?handle=&allocation_id=`.
- **Privacy.** The fan's data download (`GET /me/export`, `tickets.account_reviews` and `tickets.eligibility_decisions`)
  includes their reviews, appeals, and audit rows, but not evidence, staff notes, or which staff member decided.

## App
Fan: `/account-review` (linked from the show page and the notices). Staff: `/fraud` (queue, decisions, flag a fan,
audit lookup), linked from the profile.

## Known limitations
- No automatic detection yet: flags are raised by staff (Phase 10 "detect streaming-farm and bot behavior").
- Flags and appeals are kept when an account is deleted (for the audit trail); retention is part of the privacy review.
- A held fan can still be named as someone's +1 guest.
- Provisional product rules are listed in `PROJECT_TODO.md` (Phase 10).
