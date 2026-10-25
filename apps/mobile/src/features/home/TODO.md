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
- [ ] Provide loading, empty, offline, and error states.
- [ ] Link to artist search, journal, submissions, and profile areas as they become available.
- [ ] Respect visibility and avoid showing private activity from other users.
- [ ] Add screen tests for empty data, partial data, and navigation.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
