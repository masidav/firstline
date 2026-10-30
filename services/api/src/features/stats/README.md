# User stats API

openapi tag **Scores** / **Social** (Phase 15): a fan's numbers in one response, read from stored state and the points
ledger, never from Spotify at request time. `StatsEndpoints.LoadAsync` runs one multi-statement query; everything else
is shaping and access.

## Endpoints
| Route | Who | Shape |
| --- | --- | --- |
| `GET /me/stats` | the signed-in fan | `UserStats`: artists, listening, journal, proof, social, ledger_points |
| `GET /users/{handle}/stats` | the fan and their followers | `ProfileStats`: the same without `listening`; `403 followers_only` for anyone else, `404 not_found` for a deleted or unknown handle |

Stats are followers-only, none public by default (decided 2026-10-25). Blocking isn't wanted for now (decided
2026-10-24), so there is no block check. Following needs no approval, so "follower" means anyone who follows.

## The stat list and its sources
| Block | Stats | Source | Excludes |
| --- | --- | --- | --- |
| `artists` | raw score, percentile, rank among the artist's scored fans, scored fans, `is_estimated`, `computed_at` | `user_artist_scores` (Phase 5) | paused / removed artists |
| `listening` (own only) | artists with signals, liked tracks, playlist tracks, distinct albums, recent plays, saved albums, top tracks, followed artists, plays in the last 90 days | `user_artist_signals`, `user_album_saves`, `user_plays` (Phase 4) | inactive artists, delisted albums |
| `journal` | logs, albums logged, relistens, reviews, ratings, lists, comments | `album_logs`, `lists`, `comments` (Phase 6) | deleted logs; for viewers other than the fan also private and taken-down logs and private lists; deleted comments and comments on deleted or taken-down logs |
| `proof` | tickets, merch, screenshots | `submissions` (Phase 7) | anything not `verified` |
| `social` | followers, following | `follows` (Phase 8) | deleted accounts (account deletion also scrubs follows) |
| `ledger_points` | sum of awards | `score_events` | reversed awards (`reversed_at IS NOT NULL`); the ledger is append-only |

Comments were replaced by emoji reactions (decided 2026-10-25); the `comments` count stays for the rows that exist.

## Tests
`tests/FandomScore.Tests/StatsApiTests.cs` (counts after deletions and reversals, visibility, deleted accounts, a fan
with no data); `ProfileApiTests` covers the fan page alongside the stats route; `FeedApiTests` covers the `stat` cards
followers get on the home feed.

## Known limitations
- Rank and fan counts are computed per request with correlated subqueries over `user_artist_scores`; fine at pilot
  size, a materialised rank would be needed for large artists.
- No per-artist breakdown of journal or proof counts; the response is one fan, all artists.
