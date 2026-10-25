# TODO — Grab bag API

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Five numbered slots per fan, put/move/remove, Spotify track lookup and search with the app token, and
      followers-only reads (migration 0018, 2026-10-25). Verified: `dotnet test` (GrabBagApiTests 3 with FakeSpotify).
- [x] A song's primary artist is tracked through `track_artist(..., 'grab_bag')`, outside the daily limit. Verified:
      GrabBagApiTests and invariants.
- [ ] Bouncer door: draw a song from the grab bags of the fans a fan follows (decided 2026-10-20) and show whose it is.
- [ ] Refresh stored titles and covers when Spotify changes them (a weekly job, like the catalog refresh).
- [ ] Seed grab bags and follows in `tools/dev/seed.py` so a fresh dev database has a lively feed.

## Verification
- [x] Automated tests: `tests/FandomScore.Tests/GrabBagApiTests.cs`, `database/tests/invariants.sql` (0018 section).
