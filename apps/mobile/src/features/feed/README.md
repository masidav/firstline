# Home feed (fan app)

`Feed.tsx` is the home screen's scroll (a `FlatList`): a random mix of fans' shared photos and album logs, grab-bag
songs and stats from the fans you follow, upcoming shows of your artists, and your own fan-ticket registrations
(`GET /feed`, see `services/api/src/features/feed/README.md`). The home screen passes everything above the feed
(search, your artists, Post a photo) as the list header, and hides the feed while a search is typed.

- More loads as you scroll (`onEndReached`), with a "Show more" button as a fallback.
- Pull down, or tap **Shuffle**, for a new mix: a new `seed`; paging keeps the seed.
- `FeedCard.tsx` renders one card per type: photo, album log (with "followers only"), grab-bag song (opens in
  Spotify), concert (opens the show; says whether fan-ticket registration is open), your registration (standing
  from `ticketing/standing.ts`), and a stat sentence (`feedText.ts`).
- Other fans' cards carry a Follow / Following button (`follows/FollowButton.tsx`); one state per handle, shared by
  every card of that fan. Photos and logs by others can be reported (`ReportForm.tsx`).

Tests: `Feed.test.tsx` (cards, paging with the seed, shuffle, follow, empty state, header/inactive, reports).
