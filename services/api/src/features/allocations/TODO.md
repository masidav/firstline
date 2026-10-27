# TODO — Ticket allocations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Model events, allocation pools (ticket count or share of inventory), price (face value), eligibility thresholds, and registration windows.
      Migration 0013: shows have a hub city; allocations have a ticket count (one per fan), face value, a minimum
      percentile, and a registration window; draft → published → cancelled; rules lock once registration opens.
      Staff manage them under `/v1/admin` (`tools/dev/allocations.py` locally); fans read `/v1/artists/{id}/events`
      and `/v1/events/{id}`. Verified 2026-10-21: AllocationApiTests 16/16, invariants, full suite 173/173.
- [x] For registration and selection (from the 0013 review): one registration per fan per *show* (a show can have
      several allocations); registration takes `FOR SHARE` on the allocation so a rule edit and a registration
      serialize, and the rules lock also checks for registrations; registrations, score snapshots (percentile as
      `numeric(5,2)`, like `min_percentile`), and the selection record (seed, inputs) reference allocations with
      `RESTRICT`, never `CASCADE`; add selection state to allocations so cancelling after winners exist can be
      handled; add an IANA time zone to `cities` before the app shows local show and registration times.
      *Done 2026-10-26: most of this landed with 0013, 0017, and 0020 (unique live registration per show, `FOR SHARE`,
      `RESTRICT` everywhere, `selected_at`). Migration 0033 adds the last two: the rules lock also holds once anyone
      has registered, and every city has its IANA time zone (`HubCity.time_zone`, a show's `time_zone`). Verified:
      invariants (0033 section), AllocationConcurrencyTests.*
- [ ] Let fans register for an allocation; check eligibility against the frozen snapshot from the fraud feature.
      *Registration done 2026-10-21 (eligibility from the stored score, hub and score snapshot, one per fan per
      show, withdraw while open); the fraud snapshot waits on Phase 10.*
- [x] Implement the chosen selection method (ranked, lottery, or tiered) with a recorded seed and inputs so every draw can be audited and reproduced.
      Migration 0017: hub fans first, then by snapshot raw score, ties by a stored seed; +1 seats; raw-score
      threshold (two saved albums by default). Verified 2026-10-24: invariants, RegistrationApiTests, AllocationApiTests
      (against a local stand-in for 0015's `album_saves_score`).
- [x] Issue non-transferable reservation tokens bound to the fan's account (migration 0020, 2026-10-25): one per
      selected registration, holder fixed, token shown to the holder only. No expiry: a reservation lives until the show,
      a return, or account deletion. Identity binding beyond the account waits on Phase 10. Verified: ReservationApiTests 3,
      invariants (0020 section).
- [x] Delayed delivery (`delivery_hours_before_show`, 24 by default) and a face-value return path that hands the seats
      to the next fans in the ranking (`return_reservation()`, `backfill_allocation()`). Verified: ReservationApiTests,
      invariants. Checkout and refunds through a primary ticketer are still to build.
- [ ] Notify selected and unselected fans; release unclaimed inventory back to the promoter on schedule.
- [x] Make registration and selection idempotent and safe under on-sale traffic spikes. *Done 2026-10-26.
      Registration and the draw were already idempotent; `AllocationConcurrencyTests` now proves it under
      concurrency. Measured with `tools/load/run.sh` (pgbench, 20,000 fans): registering runs at 4,000 to 5,000 a
      second with no failures; the show page's standing was the real bottleneck (about 420 a second, growing with
      registrants) and now comes from `StandingSnapshots` (about 16,000 a second).*
- [x] Run the draw as a job for very large allocations: it takes about 7.7 s for 20,000 registrations inside the
      staff request and grows linearly, so past about 75,000 registrants it would outlast a 30 s load-balancer timeout.
      *Done 2026-10-27 (migration 0036, `workers/allocations/SelectAllocationJob.cs`): `POST .../selection` checks the
      rules and queues `allocations.select` (`202`, one per allocation); `GET .../selection` reports not_run, queued,
      running, failed, or done with the draw. Verified: SelectAllocationJobTests 3, RegistrationApiTests, invariants
      (0036), `tools/dev/allocations.py draw` polls.*
- [x] Test eligibility edges, duplicate registrations, selection reproducibility, token expiry, and transfer attempts.
      *Eligibility edges and transfer attempts were covered (RegistrationApiTests, invariants 0020); reservations have
      no expiry by design (they live until the show, a return, or deletion; delivery time is tested in
      ReservationApiTests). Added 2026-10-26 in AllocationConcurrencyTests: duplicate registrations sent at once,
      racing registrations for one show, the draw waiting for an in-flight registration, two draws at once, and the
      stored seed repeating every rank.*

## Verification
- [x] Add or update automated tests for the behavior above. *AllocationConcurrencyTests 7, invariants (0033).*
- [x] Run the relevant tests and record actual results. *2026-10-26: `dotnet test` (results in the commit), load test
      in `tools/load/README.md`.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
