# Allocations

Face-value, non-transferable tickets set aside for an artist's top fans before the public sale (Phase 11).

## Model (migration 0011)
- **Show** (`events`): an artist, a name and venue, a start time, and a hub city (`city_id`, one of the cities from
  migration 0010), so "fans whose hubs include the show's city" is an exact match.
- **Allocation** (`allocations`): a pool of tickets for a show, one per selected fan, at face value
  (`face_value_cents`, `currency`), open to fans at or above `min_percentile` for the artist, with a registration
  window (`registration_opens_at`, `registration_closes_at`, which must close before the show).
- **Lifecycle**: `draft` (staff only) → published (`upcoming`, `open`, `closed` by the clock) → optionally
  `cancelled`. Drafts are deleted rather than cancelled; publishing and cancelling can't be undone.

The database enforces the timing and locking rules with triggers (`trg_allocation_check`, `trg_allocation_lock`,
`trg_event_lock`): an allocation can only be published for an active artist and a show with a city, its rules lock
once registration opens, and an open allocation fixes the show's time, city, and artist.

## Endpoints
- Fans: `GET /v1/artists/{id}/events` (upcoming shows, `tickets_available`), `GET /v1/events/{id}` (a show and
  its published allocations).
- Staff (`/v1/admin`, staff policy, until promoter organizations exist): create and edit shows, create and edit
  allocations, publish, cancel, delete drafts.

Locally, `tools/dev/allocations.py` creates a show with a published allocation as a dev staff user.

## Not yet
Fan registration, eligibility, the ranked selection with a recorded tiebreak, reservations, ticketing partners,
and organizations. Provisional rules (one ticket per fan, percentile threshold, lock at opening) are listed as
open decisions in `PROJECT_TODO.md`.
