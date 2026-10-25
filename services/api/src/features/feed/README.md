# Home feed API

openapi tag **Feed**: `GET /feed`, the one vertical scroll the fan app shows on its home screen (2026-10-25).

## What is in it
A random mix of six sources. Each contributes its newest items (a pool of up to 100), and the pools are shuffled
together:

| Type | Who sees it | Source |
| --- | --- | --- |
| `photo` | everyone | shared verified merch and concert photos (`submissions.shared_at`) |
| `album_log` | everyone; followers-only logs to followers and their author | `album_logs`, not deleted or taken down |
| `grab_bag` | followers | `grab_bag_songs` of the fans the caller follows |
| `concert` | fans with a score for the artist, or the show's city among their hub cities | `events` starting within 120 days, with the allocation fans can register for |
| `registration` | the caller | their live `allocation_registrations` for shows that haven't started, with the standing |
| `stat` | followers | a followed fan's top scores (percentile ≥ 50), albums logged (≥ 3), verified concert photos and merch, ledger points (≥ 10) |

Deleted accounts, inactive artists, private logs, and taken-down posts never appear. Stats get a stable id from the
fan and the stat (`md5(...)::uuid`), so they page like everything else.

## Order and paging
`seed` (the app picks a random one per visit and per Shuffle; default: the UTC day) drives a hash per item. Within
a type the hash is the order; across types each type is dealt out by weight (album logs 3 per round, photos,
grab-bag songs, and stats 2, shows and registrations 1) with a seeded jitter inside the round, so every page mixes
the types even when one source has far more items, and a type that runs out leaves the page to the others. Paging
is an offset `cursor` over that order; keep the same `seed` while paging. `FeedPage.seed` echoes the seed used.

## Shape
`FeedItem.type` says which payload is set: `photo`, `log`, `grab_bag`, `concert`, `registration`, or `stat`.
`user` is null for a show; `artist` is null for a stat that spans artists; `following` says whether the caller
follows the item's user (the app's Follow button).

## Tests
`tests/FandomScore.Tests/FeedApiTests.cs`: what a follower, a stranger, and the author each see; one seed pages
consistently and another reshuffles. `ProofApiTests` and `ReportApiTests` cover photos and takedowns on the feed.
