# TODO — Home screen

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] First version (2026-10-21): signed-in fans land on `/home`, a search bar over their tracked artists that can add
      new ones from Spotify, plus a link to the profile. Verified with `npm test` (HomeScreen 6 tests).
- [ ] Define the home-screen information hierarchy based on approved product requirements.
- [x] Show relevant tracked artists and score summaries only when data is available. (2026-10-21: with an empty
      search box, `YourArtists` lists `GET /me/scores`, best percentile first, "Top 16% of fans · score 61 · estimate";
      no scores yet → connect Spotify, or how scores appear. Jest: HomeScreen 10 tests.)
- [x] Home is one vertical scroll (2026-10-25): the feed (`features/feed`) is the list, with search, your artists, and
      Post a photo as its header; the feed hides while a search is typed. More loads on scroll; Shuffle reseeds.
      Verified: Jest (HomeScreen 11, Feed 7), Chrome at phone size.
- [x] Provide loading, empty, offline, and error states. (Verified 2026-10-30 by screen tests: spinners for your
      artists and the feed while loading, the empty messages, a failed scores load with Try again, a failed feed load
      with Reload feed, and the "Can't reach the server" message when the request itself fails. Offline journaling is
      `features/album-journal`.)
- [x] Link to artist search, journal, submissions, and profile areas as they become available. (Search is the box at
      the top; Tickets, Journal, People, Profile buttons; artist rows open the artist's albums; camera opens Post a
      photo. Tested 2026-10-30.)
- [x] Respect visibility and avoid showing private activity from other users. (The server filters `GET /feed` by
      visibility (API tests); the app shows other fans' activity only through it and labels followers-only posts. A
      test pins the set of endpoints home calls, so a new per-fan source has to be reviewed. 2026-10-30.)
- [x] Add screen tests for empty data, partial data, and navigation. (`HomeScreen.test.tsx`, 17 tests, 2026-10-30;
      `Feed.test.tsx` 12.)

## Verification
- [x] Add or update automated tests. (`HomeScreen.test.tsx` 17, `Feed.test.tsx` 12, `ReactionBar.test.tsx` 5.)
- [x] Run the relevant tests and record actual results. 2026-10-30: `npm test` 51 suites, 327 tests passed;
      `npm run typecheck` clean.
- [ ] Update the corresponding README and root project checklist.
