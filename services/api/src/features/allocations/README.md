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

The database enforces the timing and locking rules with triggers (`trg_allocation_check`, `trg_allocation_delete`,
`trg_event_lock`): an allocation can only be published for an active artist and a show with a city, its rules lock
once registration opens, a published allocation keeps its show's artist and city (and once open, its time), and
published allocations are cancelled, never deleted.

## Registrations
A fan registers while registration is open if the artist is active, they have a hub city and a score for the
artist, and their percentile meets `min_percentile` (`registration_blocker()` says which check fails). One live
registration per fan per show, across its allocations; registering again returns the same one. Each registration
keeps a snapshot: the fan's hub cities (and where the show's city sits in them, `hub_match`), percentile, raw
score, and whether it was an estimate. Withdrawing is allowed while registration is open and keeps the row;
deleting an account withdraws its registrations. Registrations are never edited or deleted.

## Endpoints
- Fans: `GET /v1/artists/{id}/events` (upcoming shows, `tickets_available`), `GET /v1/events/{id}` (a show and
  its published allocations), `GET /v1/allocations/{id}/eligibility`, `PUT`/`DELETE /v1/allocations/{id}/registration`,
  `GET /v1/me/registrations`.
- Staff (`/v1/admin`, staff policy, until promoter organizations exist): create and edit shows, create and edit
  allocations, publish, cancel, delete drafts.

Locally, `tools/dev/allocations.py` creates a show with a published allocation as a dev staff user.

## Not yet
The ranked selection with a recorded tiebreak, reservations, ticketing partners,
and organizations. Provisional rules (one ticket per fan, percentile threshold, lock at opening) are listed as
open decisions in `PROJECT_TODO.md`.
