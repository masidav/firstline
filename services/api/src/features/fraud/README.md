# Fraud review, appeals, and the eligibility audit

Migration `0042_fraud_review.sql`, endpoints in `FraudEndpoints.cs`. Tests: `FraudReviewApiTests`, invariants (0042).

## What it does
- **Flags.** Staff flag a fan (`POST /v1/admin/fraud-flags`) with a reason (`streaming_farm`, `bot`, `shared_device`,
  `duplicate_account`, `other`), an internal note, and optional evidence (JSON). One open flag per fan.
- **Automatic detection** (migration 0043, worker `fraud.detect` every 6 hours): rules in `fraud_rules`
  (`round_the_clock`, `rapid_plays`, `looping`, `listening_spike`; `burst_engagement` since 0045, over likes and album
  saves) run over fans with plays or saves in the last day and raise a flag with `source = 'rule'`, the rules that
  fired, and what they measured in `evidence.rules`. The flag is named by the strongest reason: bot, then streaming_farm.
  A fan already held isn't flagged again; a rule whose flag staff cleared waits `cooldown_days` (30). Thresholds are
  data (`fraud_rules.params`), provisional. Strategies and rules: `docs/security/gaming-strategies.md`.
- **Hidden until reviewed** (migration 0044, decided 2026-10-30): a rule flag isn't shown to the fan until staff uphold
  it (`shown_to_fan_at`). Until then: no notice, nothing at `/me/fraud-review` or in the data download, no appeal, and
  registering is refused with the neutral `registration_unavailable` ("Registration isn't available for your account
  right now"). Upholding tells the fan (`fraud_hold`); clearing it unreviewed tells them nothing. Staff get
  `staff_flag_before_draw` when a rule flags a fan who's in a draw still to run, and the queue lists those first
  (`context.next_draw_closes_at`).
- **The hold.** While a flag is `open` or `upheld` (`fan_on_hold()`), the fan can't register
  (`registration_blocker()` answers `under_review`; the API returns `403 under_review`). The full hold applies to an
  upheld flag, or an open one staff raised (`fan_full_hold_flag()`; an open *rule* flag only blocks registering,
  decided 2026-10-29). Under the full hold the fan also:
  - is withdrawn from any allocation the draw runs (`withdrawn_reason = 'fraud_hold'`, before the trust snapshots and
    ranking, so the draw stays reproducible from stored data);
  - is skipped when a returned seat is handed on (`backfill_allocation()`).

  A registration made before the flag stays live until the draw, so clearing the flag in time restores it. Tickets
  already given out are **not** taken back (decided 2026-10-29).
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
- Detection reads only the play ledger: no device, network, or payment signals, so linked accounts and "click
  everything" gaming aren't detected (see `docs/security/gaming-strategies.md`).
- Flags and appeals are kept when an account is deleted (for the audit trail); retention is part of the privacy review.
- A held fan can still be named as someone's +1 guest.
- The product rules (confirmed 2026-10-29) are listed in `PROJECT_TODO.md` (Phase 10). The migration's header still
  calls keeping tickets an open decision; it was decided as written.
