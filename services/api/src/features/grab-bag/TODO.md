# TODO — Grab bag API

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Five numbered slots per fan, put/move/remove, Spotify track lookup and search with the app token, and
      followers-only reads (migration 0018, 2026-10-25). Verified: `dotnet test` (GrabBagApiTests 3 with FakeSpotify).
- [x] A song's primary artist is tracked through `track_artist(..., 'grab_bag')`, outside the daily limit. Verified:
      GrabBagApiTests and invariants.
- [x] Bouncer door (2026-10-25): `GET /me/bouncer-door` draws a song at random from followed fans' grab bags with its
      owner; app screen `/bouncer-door` from the home header and profile. Verified: GrabBagApiTests, app Jest (BouncerDoorScreen 2).
- [x] Refresh stored titles and covers (2026-10-25): worker `grab_bag.refresh_songs`, daily, re-reads songs not refreshed
      for 7 days (migration 0019 `refreshed_at`). Verified: GrabBagRefreshJobTests 2.
- [x] Seed grab bags and follows in `tools/dev/seed.py` (2026-10-25), plus shared merch and some followers-only logs.
      Verified: applied to a fresh database (824 follows, 366 songs, 30 shared photos with the default seed).

## Verification
- [x] Automated tests: `tests/FandomScore.Tests/GrabBagApiTests.cs`, `GrabBagRefreshJobTests.cs`, `database/tests/invariants.sql` (0018 section).
