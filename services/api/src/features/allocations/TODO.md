# TODO — Ticket allocations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Model events, allocation pools (ticket count or share of inventory), price (face value), eligibility thresholds, and registration windows.
      Migration 0011: shows have a hub city; allocations have a ticket count (one per fan), face value, a minimum
      percentile, and a registration window; draft → published → cancelled; rules lock once registration opens.
      Staff manage them under `/v1/admin` (`tools/dev/allocations.py` locally); fans read `/v1/artists/{id}/events`
      and `/v1/events/{id}`. Verified 2026-10-21: AllocationApiTests 16/16, invariants, full suite 173/173.
- [ ] For registration and selection (from the 0011 review): one registration per fan per *show* (a show can have
      several allocations); registration takes `FOR SHARE` on the allocation so a rule edit and a registration
      serialize, and the rules lock also checks for registrations; registrations, score snapshots (percentile as
      `numeric(5,2)`, like `min_percentile`), and the selection record (seed, inputs) reference allocations with
      `RESTRICT`, never `CASCADE`; add selection state to allocations so cancelling after winners exist can be
      handled; add an IANA time zone to `cities` before the app shows local show and registration times.
- [ ] Let fans register for an allocation; check eligibility against the frozen snapshot from the fraud feature.
- [ ] Implement the chosen selection method (ranked, lottery, or tiered) with a recorded seed and inputs so every draw can be audited and reproduced.
- [ ] Issue non-transferable reservation tokens bound to the fan's verified identity, with expiry.
- [ ] Support delayed ticket delivery and a face-value exchange or return path.
- [ ] Notify selected and unselected fans; release unclaimed inventory back to the promoter on schedule.
- [ ] Make registration and selection idempotent and safe under on-sale traffic spikes.
- [ ] Test eligibility edges, duplicate registrations, selection reproducibility, token expiry, and transfer attempts.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
