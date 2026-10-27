# Concert data (Ticketmaster first)

Upcoming shows, sale dates, and presales for tracked artists, loaded into `events` (migration 0030). Decided
2026-10-26: Ticketmaster, SeatGeek, Bandsintown, and Songkick will all be sources; Ticketmaster is built first.

## Jobs
| Kind | When | What |
| --- | --- | --- |
| `concerts.sync_artist` | Queued by the schedule (artists never looked up go first, so a new artist's shows arrive within the hour) and when staff link an artist. Payload `{"artist_id": ...}`. | `ConcertSyncJob`: link the artist to a Ticketmaster attraction if needed, then insert or update every upcoming show in `Ticketmaster__Countries` by its Ticketmaster id. |
| `concerts.schedule` | Hourly. | `ScheduleConcertSyncsJob`: queues artists whose shows are older than `SyncEvery` (1 day), never looked up, or unmatched for longer than `RecheckUnmatchedEvery` (7 days); at most `MaxSyncsPerHour` (100) a run. |

## Linking an artist
- The worker searches Ticketmaster's attractions by the artist's name and links **only** an attraction whose
  `externalLinks.spotify` lists the artist's Spotify page (`matched_by = spotify_link`). Same-named bands are never
  guessed. No match is recorded (`external_id` null) and looked up again weekly.
- Staff link the rest in the app (Profile → Concert sources) or with `PUT /v1/admin/artists/{id}/sources/ticketmaster`,
  using the Discovery attraction id (from the
  Discovery API or its explorer; not the number in a ticketmaster.com URL). `DELETE` removes a wrong link.

## What a show gets
- Name, venue, city, state or province, country, and the hub city when the venue's city is one (`hub_city_for`). A
  venue outside the hub-city list has none and reaches fans through their score; no suburb mapping is planned
  (decided 2026-10-27: fans' backup hub cities cover nearby places).
- `starts_at` from Ticketmaster's UTC time, or its local date and time in the venue's time zone; with only a date it is
  local midnight and `time_tba` is set.
- `status` (onsale, offsale, cancelled, postponed, rescheduled), `ticket_url`, the public sale window, and presales
  (replaced on every sync). Cancelled shows stay listed but leave the feed.
- A show with an allocation keeps the time and city staff locked (`trg_event_lock`); Ticketmaster's time goes to
  `source_starts_at`. A new source time, or the show turning cancelled, postponed, or rescheduled, sends staff a
  `staff_show_changed` notice (migration 0036), once per change.
- A co-headline show already loaded for another artist stays with that artist.
- Shows Ticketmaster stops listing are left as they are (they may carry allocations).

## Limits and terms
- The Discovery API's default quota is 5,000 calls a day and 5 a second. Each artist costs about two calls a day;
  the client spaces calls 250 ms apart per process, and a 429 or server error retries through the job queue.
- **Terms (to check before launch):** Ticketmaster's general API terms say an app may not "derive revenues from the
  use or provision of the Ticketmaster API", may cache event content only "for reasonable periods", must remove
  content within 24 hours on request, and may be rate-limited for calls "not primarily in response to direct user
  actions". Fandom Score plans fees, so a partner agreement may be needed (`PROJECT_TODO.md`, Open decisions).

## Configuration
| Setting | Default |
| --- | --- |
| `Ticketmaster__ApiKey` | empty: concert syncing off (secret; developer.ticketmaster.com) |
| `Ticketmaster__Countries` | `US,CA` |
| `Ticketmaster__SyncEvery` / `Ticketmaster__RecheckUnmatchedEvery` | `1.00:00:00` / `7.00:00:00` |
| `Ticketmaster__MaxSyncsPerHour` | `100` |

Tests: `tests/FandomScore.Tests/ConcertSyncJobTests.cs` (a fake Discovery API in Ticketmaster's JSON shapes),
`ConcertApiTests.cs` (staff links, show fields), invariants for 0030.
