# TODO — Mobile application

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect current Expo/React Native setup, navigation, state management, styling, and build scripts. (New Expo SDK 57 app with expo-router; web first.)
- [ ] Establish shared UI patterns, theme tokens, loading states, empty states, and error states.
- [x] Set up typed API client and centralized session handling using the project's existing language conventions. (`src/api`, `src/auth/session.ts`)
- [ ] Add navigation and screens for the features in the product specification.
- [x] Implement secure handling of authentication state and avoid storing secrets in plain text. (Keychain on iOS; localStorage on web, with a known limit)
- [ ] Support offline album-journal writes and a retryable synchronization queue.
- [ ] Add accessibility labels, keyboard handling, and responsive layouts for mobile devices.
- [ ] Handle API errors, network loss, expired sessions, and background/resume behavior.
- [ ] Add component and screen tests using the existing test stack.
- [ ] Verify iOS and Android builds and document required configuration. *Web build verified (CI and a Playwright WebKit run at iPhone size, 2026-10-18); iOS waits on the Apple Developer Program.*
- [ ] Google sign-in on web (needs a Google OAuth web client ID).
- [ ] Sign in with Apple and Apple re-authentication before account deletion (iOS).

## Home screen concept (requested 2026-10-20, not designed yet)
- [ ] Main feed scrolls **horizontally, like a music sheet** (a staff the fan moves along).
- [ ] A **red velvet rope** as a visual motif (for example, along the staff or guarding the door below).
- [ ] Non-music items break from the sheet and **bounce in vertically**: reviews from people the fan follows,
      upcoming concerts (in the fan's hub cities), merch someone shared.
- [x] After some scrolling, a **bouncer at a door**. Tapping the door shows a real track from the **grab bag**
      of someone the fan follows, with that fan's name and a link to the song on Spotify. *Done 2026-10-25: the door is
      a card in the feed after the sixth item (or at the end of a shorter feed); "Knock" draws a song inline
      (`BouncerDoorCard`). Verified: Feed tests, clicked through in Chrome at phone size.*
- [ ] **Grab bag** screen: pick up to 5 favorite songs (any artist), reorder or remove them. Shared with
      followers; sharing can't be turned off. *Built 2026-10-25 (`/grab-bag`) except reordering.*
- [x] **Change "Post a photo" to something better** (requested 2026-10-25): *done 2026-10-25 as a round camera button
      floating over the feed (drawn with views; no icon font), hidden while searching. Verified: HomeScreen tests, Chrome.*
- [x] **Use real artists and albums for dev testing** (requested 2026-10-25): *done 2026-10-25: with Spotify credentials
      `tools/dev/seed.py` fetches real artists (`REAL_ARTISTS`), their newest albums, covers, and tracks with the app
      token; grab bags use real track ids. Without credentials (CI, or `--fake`) it falls back to made-up artists.
      Verified with a faked Spotify (`tools/ci/test_seed_catalog.py`) and both variants applied to a fresh database;
      not yet run against real Spotify (no credentials in the build environment).*
- [ ] **Music trivia** (requested 2026-10-25): quick questions about the fan's artists in the feed (for example which
      album a song is on, or the release year), answered in a tap. Open: where questions come from (the stored
      catalog can generate many), whether answers earn points (they are an attack surface if they do), and how often.
- [ ] **Music prompts** (requested 2026-10-25): open questions fans answer for their followers, for example "What's
      your best hypothetical band name?", shown in the feed with the answers from people the fan follows. Answers are
      posts, so they need reporting and takedowns like logs and photos.
- [ ] **Featured artist of the week** (requested 2026-10-25): one artist highlighted at the top of the feed for a
      week. Open: who picks (staff, the most-followed artists, or a promoter placement, which would need disclosure),
      and what the feature shows (top fans, upcoming fan-ticket shows, a grab-bag song).
- [x] Sign-up: choose a primary hub city (required), plus up to two backups.
- [x] Changing hub cities: allowed once a week; warn before saving (next change possible in 7 days, and hub
      cities decide which allocations the fan qualifies for).
- [ ] Profile: followers and following lists, remove a follower, block someone. *Lists and remove built 2026-10-25
      (`/people`); blocking not wanted for now (decided 2026-10-24).*
- [ ] Respect reduced-motion settings: the bounce and horizontal scroll need a calmer fallback, and the feed
      must work with VoiceOver and TalkBack.

## Ticket allocations (Phase 11)
- [ ] Show upcoming allocations for followed artists and the fan's eligibility, with what to do to qualify.
- [ ] Register for an allocation; show status (registered, selected, not selected, expired).
- [ ] Claim a reservation and hand off to checkout; show the non-transferable ticket or delivery date.
- [ ] Face-value exchange or return flow.
- [ ] Release builds via EAS Build; beta via TestFlight and Google Play closed testing.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
