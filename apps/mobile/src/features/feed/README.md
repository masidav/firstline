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

## Screen readers and motion

- Every tappable element has a role and a label that says what is on it, not just where it goes: a byline is a link
  that reads its whole sentence ("Ben's grab bag, song 2, Superfan"), with the fan's page as the hint; the concert,
  registration and album rows read their name, date, place and status, with "Opens the show" or "Opens the album" as
  the hint. Reaction chips read their count and whether they are yours (`selected`).
- Decorative pictures are hidden from VoiceOver, TalkBack and `aria` (`ui/decorative.ts`): the avatar beside a name,
  album covers beside a title, the bouncer's door. A fan's photo is content and is described.
- Nothing in the feed moves by itself today (the vertical scroll is the fan's own; the music-sheet scroll and the
  bounce-in of non-music items in `TODO.md` aren't built). Anything that does move takes `ui/useReducedMotion.ts`
  (Reduce Motion on iOS, "Remove animations" on Android, `prefers-reduced-motion` on the web) and shows its final
  state instead: screen transitions cross-fade (`app/_layout.tsx`) and the now-playing progress line holds still
  between polls (`spotify/NowPlaying.tsx`).

Tests: `Feed.test.tsx` (cards, paging with the seed, shuffle, follow, empty state, header/inactive, reports, what a
screen reader gets), `ReactionBar.test.tsx`.
