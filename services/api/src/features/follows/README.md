# Follows API

openapi tag **Social** (migration 0018, decided 2026-10-20): follow/follower relationships between fans. Following
needs no approval; a fan can unfollow, or remove a follower. Blocking is not wanted for now (decided 2026-10-24).

## What followers get
- Followers-only album logs (`Journal.VisibleToViewer`, `GET /users/{handle}/logs`, `GET /logs/{id}`, and the feed).
- The fan's grab bag (`GET /users/{handle}/grab-bag`).
- The fan's top fandom stats, as `stat` items on the home feed (`features/feed`).

## Endpoints
| Route | What |
| --- | --- |
| `GET /users?q=` | find fans by handle or display name (caller left out) |
| `GET /users/{handle}` | public page: counts and how the fan and the caller relate |
| `PUT` / `DELETE /users/{handle}/follow` | follow / unfollow (idempotent; 422 for yourself, 404 for no such account) |
| `GET /me/following`, `GET /me/followers` | newest first |
| `DELETE /me/followers/{handle}` | remove a follower |

Account deletion removes the account's follows both ways (`users_social_scrub`); a deleted account can neither
follow nor be followed (`follows_live_users`). Invariants in `database/tests/invariants.sql`; API tests in
`tests/FandomScore.Tests/FollowApiTests.cs`.
