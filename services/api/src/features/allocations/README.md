# Allocations

Face-value, non-transferable tickets set aside for an artist's top fans before the public sale (Phase 11).

## Model (migration 0013)
- **Show** (`events`): an artist, a name and venue, a start time, and a hub city (`city_id`, one of the cities from
  migration 0010), so "fans whose hubs include the show's city" is an exact match.
- **Allocation** (`allocations`): a pool of seats for a show at face value (`face_value_cents`, `currency`): one
  per selected fan, two with a +1. Open to fans whose raw score for the artist is at least `min_raw_score`, with a
  registration window (`registration_opens_at`, `registration_closes_at`, which must close before the show).
  Published without a threshold, it gets `allocation_default_threshold()`: the raw score of a fan whose only signal
  is two saved albums (`album_saves_score()` from migration 0015), fixed at publishing (migration 0017).
- **Lifecycle**: `draft` (staff only) → published (`upcoming`, `open`, `closed` by the clock) → `selected` once the
  draw has run, or `cancelled` before that. Drafts are deleted rather than cancelled; publishing, cancelling, and the
  draw can't be undone.

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

## +1 and guests (migration 0017)
A fan asks for a +1 when registering (`PUT .../registration` with `{"plus_one": true}`) and can turn it off or on
while registration is open; a selected fan can add one after the draw while seats are left (`add_plus_one()`). The
guest needs an account, which can come later: the fan names it by handle (`PUT .../registration/guest`), once
(`name_plus_one_guest()`). A guest can't be the fan, registered for the same show, or another fan's guest for it, and a
named guest can't register for that show.

## The draw (migration 0017; a worker job since 0036)
Staff queue it after registration closes (`POST /v1/admin/allocations/{id}/selection`, `202`) and poll
`GET .../selection` until `status` is `done`; the worker runs `run_allocation_selection()`
(`workers/allocations/SelectAllocationJob.cs`, job `allocations.select`, one queued per allocation). It runs in the
background because a draw is linear in the registrations (about 0.4 ms each, `tools/load/README.md`) and nothing
depends on how soon it runs: the snapshots and the stored seed fix the ranking. `allocation_selection_blocker()`
gives staff the refusals (still open, cancelled, artist paused) at once; a failed job is queued again by the next
`POST`.
`allocation_ranking(allocation, seed)` orders live registrations: hub fans (the show's city in their three hub cities)
first, then the rest; each by snapshot raw score; ties by `md5(seed || registration id)`. Seats go down the ranking,
two for a +1; with one seat left, a fan with a +1 gets it alone (provisional). The seed and counts are stored once in
`allocation_selections`, so anyone can rerun the ranking and get the same order. Outcomes, ranks, and seats are written
only by the draw (a guard trigger checks a transaction setting), and a selected allocation can't be cancelled.

## Standing ("you'll most likely get a ticket")
`GET /v1/allocations/{id}/eligibility` includes `standing` while registration is upcoming or open: the fan's rank if
the draw ran now (ranked like the draw, but equal scores count against the fan), the top percent, and `likely`.
Other fans come from an in-memory snapshot of the allocation's live registrations (`StandingSnapshots`), at most
`Allocations:StandingSnapshotSeconds` old (5 by default; 0 reads fresh every time) and loaded by one query at a
time per pod, so fans refreshing the show page during an on-sale don't each count every registration
(`tools/load/README.md`: about 420 standings a second before, about 16,000 now, at 20,000 registrants). It is
true only when the seats of everyone ranked ahead plus the fan's own (2 with a +1) fit within `tickets` minus a
margin, `Allocations:LikelyMarginSeats` (25 to start, decided 2026-10-24), which leaves room for fans who register
later with higher scores. `?plus_one=true` projects a +1 for a fan who hasn't registered yet. The app shows it on the
show screen above the Register button.

## Reservations (migration 0020)
A registration that becomes selected gets a **reservation** (`reservations`, written by a trigger): the fan's seats
at the allocation's face value, with a 64-hex **token**.
- **Non-transferable**: the holder never changes; the API shows the token only to the holder (`GET /me/reservations`,
  `GET /reservations/{id}`), the named +1 sees the reservation without it, staff lists never include it.
- **Delayed delivery**: the token is withheld until `delivery_hours_before_show` before the show (24 by default; a
  rule like the others, so it locks once registration opens). Until then `status` is `held` with `deliver_at`.
- **Face-value exchange**: `POST /reservations/{id}/return` (`return_reservation()`) until the show starts, idempotent.
  The seats go, at the same price, to the next not-selected fans down the draw's ranking (`backfill_allocation()`,
  same +1 rule as the draw); the fan's registration then reads `returned`. Account deletion cancels the account's
  reservations and hands the seats on the same way (`cancel_user_reservations()`). `seats_assigned` on the recorded
  draw moves accordingly; the draw's own counts and seed don't.
- Money doesn't move yet: checkout through a primary ticketer is the next Phase 11 item.
`trg_reservation_guard` keeps all of this: no hand-made rows, no change of holder, price, token, or delivery time,
returns and cancellations only through their functions, never deleted or truncated.

## Concurrency (tested in `AllocationConcurrencyTests`, measured in `tools/load`)
- The same registration sent many times at once registers the fan once; two registrations for different
  allocations of one show at once leave one, and the other gets `already_registered`.
- The draw locks the allocation `FOR UPDATE`, so it waits for registrations still in flight (they hold it `FOR SHARE`)
  and counts them; two draws at once record one, with one seed, and `allocation_ranking()` with that seed repeats
  every stored rank.
- An allocation's rules lock once registration opens or once anyone has registered, whichever comes first
  (migration 0033).
- Registering runs at about 4,000 to 5,000 a second on a 12-core machine and flattens past 50 concurrent clients;
  the draw takes about 7.7 s for 20,000 registrations (see `tools/load/README.md`), in the worker since 0036 so a
  very large one can't outlast a request timeout (the job lease is 5 minutes, about 750,000 registrations; a
  re-claimed job waits on the allocation lock and finds the recorded draw).

## Time zones (migration 0033)
Every hub city has its IANA time zone (`cities.time_zone`, checked against PostgreSQL's zone list for new cities).
`HubCity.time_zone` and a show's `time_zone` (null without a hub city) let the app show local times.

## Endpoints
- Fans: `GET /v1/artists/{id}/events` (upcoming shows, `tickets_available`), `GET /v1/events/{id}` (a show and
  its published allocations), `GET /v1/allocations/{id}/eligibility`, `PUT`/`DELETE /v1/allocations/{id}/registration`,
  `PUT /v1/allocations/{id}/registration/guest`, `GET /v1/me/registrations`, `GET /v1/me/reservations`,
  `GET /v1/reservations/{id}`, `POST /v1/reservations/{id}/return`.
- Staff (`/v1/admin`, staff policy, until promoter organizations exist): create and edit shows, create and edit
  allocations (including `delivery_hours_before_show`), publish, cancel, delete drafts, queue the draw and read its state, list
  registrations in ranking order, and list who holds seats (`GET /admin/allocations/{id}/reservations`).

Locally, `tools/dev/allocations.py` creates a show with a published allocation as a dev staff user, and can close
registration (`close`) and run the draw (`draw`).

## Not yet
Reservations and tickets (delivery to the fan and the named guest), notifying fans of the outcome, giving unclaimed
seats back, ticketing partners, and organizations. Provisional rules are listed as open decisions in
`PROJECT_TODO.md`: a +1 that doesn't fit gets the fan a single seat; a guest can't be changed; a selected fan can't
drop a +1 after the draw; a +1 can be added after the draw until the show starts.
