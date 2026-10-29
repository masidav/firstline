# API security pass, 2026-10-28

A review of every `/v1` endpoint (object-level authorization, visibility, signed links, SQL, input limits, rate limits,
sessions, errors, CORS), plus standing tests so regressions fail CI.

## Standing guards (tests)
- `SecurityRouteTests`: only 8 reviewed routes skip sign-in (listed with their reasons); every `/admin` route needs the
  staff policy; `openapi.yaml`'s `security: []` matches; the running API answers 401 without sign-in on every protected
  route (over 100) and 403 to a fan on every admin route.
- `DuplicateRequestTests`: follow, reaction, prompt answer, push device, report, and opening trivia sent 8 times at
  once leave one row; answering trivia at once scores once.
- `SecurityFixTests`, `RegistrationApiTests` (guest decline): the fixes below.

## Found fine
No SQL injection (every interpolated fragment is a constant or from a fixed list; LIKE input escaped). No IDOR: every
id-taking route scopes by the caller in SQL or a database function. Signed links separate purposes. Paging and cursors
are bounded. Sessions are checked on every request; refresh tokens rotate and reuse revokes; deletion stops tokens at
once; staff is re-read from the database. Problem details hide internals outside Development. CORS uses an explicit
origin list.

## Fixed
| Finding | Severity | Fix |
| --- | --- | --- |
| Naming someone as your +1 blocked them from registering, with no way out (lock rivals out of a draw) | High | Migration 0041: the guest is notified (`guest_named`), can decline before the draw (`POST /events/{id}/guest-invite/decline`), and can't be named again by that fan for that show; registration says `named_as_guest`. App: a Decline button on the show. |
| Grab-bag adds unlimited (a Spotify call and maybe a new artist each) | Medium | 30 per hour per fan |
| Album search falls through to Spotify with no limit | Medium | Shares artist search's 30 per minute |
| Score-card PNGs drawn on every hit, anonymously | Low-medium | 60 per minute (link per fan, image per IP); drawn images cached 10 minutes |
| Artist-wide rescore could be kept running | Low | 10 per hour per fan |
| Removing a reaction answered with counts for posts the caller can't see | Low | Same visibility check as adding (404) |
| Others' stats and profile counted private and taken-down logs and private lists | Low | Counted only for the fan themselves |
| Request bodies up to 30 MB on every JSON endpoint | Low | 1 MB, uploads raise it to their declared size |
| No general limit | Low | 1,200 requests per minute per fan (or IP); follows, unfollows, and clicks 120 per minute |
| A signed photo link copied from the feed worked for up to an hour after a takedown (fixed 2026-10-29) | Low-medium | The feed's file links have their own purpose (`shared_file`) and `GET /files/{id}` checks them on every request (one primary-key join): the photo must still be on the feed (shared, not taken down, verified, author not deleted, artist active, file archived), else 404. Their Cloud Storage redirect lasts 2 minutes and is cached 60 seconds (owner and staff links keep 15 minutes and 10). Links for the owner (submissions, data export) and staff (review, reports, redaction) are unchanged: a taken-down photo stays as proof. Unsharing stops feed links too. Prompt answers and album logs have no files; featured videos were already checked on every request. Tests: `ReportApiTests` (2 new). |

## Decided (2026-10-28)
- A named +1 doesn't have to accept; declining before the draw is the safety net.
- Follows need no approval: followers-only content is for anyone who follows.
- Others' prompt answers may be seen before you answer (feed, answers list); only the Today card hides them.

## Open (later work; in PROJECT_TODO.md)
- A push token moves to whoever registers it last (by design); a stolen token could receive someone's pushes.
- Set `AllowedHosts` (or a configured public origin) for deployed environments; links are built from the Host header.
- Require the sign-in nonce once the app always sends one.
- Not reviewed: worker job handlers beyond the redaction gate.
