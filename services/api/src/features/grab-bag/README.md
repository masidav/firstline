# Grab bag API

openapi tag **Social** (migration 0018, decided 2026-10-20): each fan can put **five favourite songs** in a grab
bag, in numbered slots. Grab bags are **shared with followers** (it can't be turned off), the owner's name is shown
with each song, and each song **links to Spotify**. Followers see the songs on their home feed (`features/feed`).
The bouncer door draws one of those songs at random for the fan.

## Endpoints
| Route | What |
| --- | --- |
| `GET /me/grab-bag` | the caller's bag: filled slots in order, and how many slots there are |
| `PUT /me/grab-bag/{position}` | put a song (`spotify_track_id`) in slot 1–5, replacing what was there; a song already in another slot moves |
| `PUT /me/grab-bag/order` | reorder: the five slots in order, each song id once, null for an empty slot |
| `DELETE /me/grab-bag/{position}` | empty a slot |
| `GET /users/{handle}/grab-bag` | a fan's bag, for the owner and their followers (`403 followers_only` otherwise) |
| `GET /tracks/search?q=` | Spotify track search with the app token (same rate limit as artist search), tracked artists marked |
| `GET /me/bouncer-door` | a song drawn at random from followed fans' grab bags, with its owner; null with counts when there is none |

## Artists
The song is looked up on Spotify (`SpotifyClient.GetTrackAsync`), and its primary artist is tracked through
`track_artist(..., p_via => 'grab_bag')`: a new artist starts `onboarding` like any other add, but **doesn't count
toward the fan's 50-artists-per-day limit** (`artists.added_via`). The song's title, album, and cover are stored on
the row because the artist's catalog may not be loaded yet.

The worker's `grab_bag.refresh_songs` job (daily, `workers/grab-bag`) re-reads songs not refreshed for a week so
titles and covers stay current (migration 0019). Account deletion empties the bag at once (`users_social_scrub`). Invariants in `database/tests/invariants.sql`;
API tests in `tests/FandomScore.Tests/GrabBagApiTests.cs` (with `FakeSpotify`).
