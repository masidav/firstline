# TODO — Ticket allocations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Model events, allocation pools (ticket count or share of inventory), price (face value), eligibility thresholds, and registration windows.
      Migration 0013: shows have a hub city; allocations have a ticket count (one per fan), face value, a minimum
      percentile, and a registration window; draft → published → cancelled; rules lock once registration opens.
      Staff manage them under `/v1/admin` (`tools/dev/allocations.py` locally); fans read `/v1/artists/{id}/events`
      and `/v1/events/{id}`. Verified 2026-10-21: AllocationApiTests 16/16, invariants, full suite 173/173.
- [ ] For registration and selection (from the 0013 review): one registration per fan per *show* (a show can have
      several allocations); registration takes `FOR SHARE` on the allocation so a rule edit and a registration
      serialize, and the rules lock also checks for registrations; registrations, score snapshots (percentile as
      `numeric(5,2)`, like `min_percentile`), and the selection record (seed, inputs) reference allocations with
      `RESTRICT`, never `CASCADE`; add selection state to allocations so cancelling after winners exist can be
      handled; add an IANA time zone to `cities` before the app shows local show and registration times.
- [ ] Let fans register for an allocation; check eligibility against the frozen snapshot from the fraud feature.
      *Registration done 2026-10-21 (eligibility from the stored score, hub and score snapshot, one per fan per
      show, withdraw while open); the fraud snapshot waits on Phase 10.*
- [x] Implement the chosen selection method (ranked, lottery, or tiered) with a recorded seed and inputs so every draw can be audited and reproduced.
      Migration 0017: hub fans first, then by snapshot raw score, ties by a stored seed; +1 seats; raw-score
      threshold (two saved albums by default). Verified 2026-10-24: invariants, RegistrationApiTests, AllocationApiTests
      (against a local stand-in for 0015's `album_saves_score`).
- [x] Suspensions (decided 2026-10-24): suspended accounts can't register or be a +1; a host suspended after registering
      is ranked and, if selected, held (`on_hold`) with no +1 changes until staff resolve it. Migration 0019. Verified
      2026-10-24: invariants, RegistrationApiTests, full suite 212/212.
- [ ] Issue non-transferable reservation tokens bound to the fan's verified identity, with expiry.
      Ticket delivery and a guest claiming their seat must refuse suspended accounts (holds above).
- [ ] Support delayed ticket delivery and a face-value exchange or return path.
- [ ] Notify selected and unselected fans; release unclaimed inventory back to the promoter on schedule.
- [ ] Make registration and selection idempotent and safe under on-sale traffic spikes.
- [ ] Test eligibility edges, duplicate registrations, selection reproducibility, token expiry, and transfer attempts.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
