# TODO — Mobile application

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect current Expo/React Native setup, navigation, state management, styling, and build scripts. (New Expo SDK 57 app with expo-router; web first.)
- [ ] Establish shared UI patterns, theme tokens, loading states, empty states, and error states.
- [x] Set up typed API client and centralized session handling using the project's existing language conventions. (`src/api`, `src/auth/session.ts`)
- [ ] Add navigation and screens for the features in the product specification.
- [x] Implement secure handling of authentication state and avoid storing secrets in plain text. (Keychain on iOS; localStorage on web, with a known limit)
- [x] Support offline album-journal writes and a retryable synchronization queue. *Done 2026-10-26
      (`src/features/album-journal/journalQueue.ts`); details and verification in the album-journal TODO.*
- [ ] Add accessibility labels, keyboard handling, and responsive layouts for mobile devices.
- [ ] Handle API errors, network loss, expired sessions, and background/resume behavior.
- [ ] Add component and screen tests using the existing test stack. *Plus browser end-to-end journeys since
      2026-10-27 (`e2e/`, run in CI).*
- [ ] Verify iOS and Android builds and document required configuration. *Web build verified (CI and a Playwright
      WebKit run at iPhone size, 2026-10-18). iOS configured 2026-10-29: bundle ID `com.firstline.fandomscore`,
      icon and splash (`assets/`, drawn by `tools/dev/app-icons.mjs`), `app.config.ts` (Google scheme, EAS project id,
      local-network exception for dev builds), `eas.json` profiles, `expo-dev-client`. Verified: `npm run check:ios`
      (iOS prebuild and Hermes bundle on Linux, now in CI), typecheck, Jest 297. The build itself waits on the Apple
      Developer Program and `eas init` (README "Building the iOS app"). Android: package and adaptive icon set, never built.*
- [x] Google sign-in on web. *Done 2026-10-26: "Continue with Google" sends the fan to Google (OpenID Connect,
      ID token in the fragment, no Google script), `/google-callback` checks the state and signs in; a new account
      chooses a handle. Needs `EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID` and the redirect URI on the Google client (README).
      Verified: providerSignIn and SignInScreen tests; in Chrome against a dev API, with a stubbed Google page: new
      account, returning account, forged state refused, access_denied explained. Not yet tried with the real Google
      client: the OAuth consent screen is in Testing, so testers must be added as test users (or the screen published).*
- [x] Sign in with Apple and Apple re-authentication before account deletion (iOS). *Written 2026-10-26
      (`expo-apple-authentication`, hashed nonce, fresh authorization code before `DELETE /me`); unit-tested with the
      module mocked. Not run on a device: needs the Apple Developer Program and an iOS build.*
- [ ] Google sign-in in the native app: written 2026-10-26 (`googleSignIn.native.ts`, PKCE code flow with an iOS
      client ID, unit-tested), not run on a device. Needs an iOS client ID; `app.config.ts` adds its reversed scheme (2026-10-29).

## Home screen concept (requested 2026-10-20, not designed yet)
- [ ] Main feed scrolls **horizontally, like a music sheet** (a staff the fan moves along).
- [ ] A **red velvet rope** as a visual motif (for example, along the staff or guarding the door below).
- [ ] Non-music items break from the sheet and **bounce in vertically**: reviews from people the fan follows,
      upcoming concerts (in the fan's hub cities), merch someone shared.
- [x] After some scrolling, a **bouncer at a door**. Tapping the door shows a real track from the **grab bag**
      of someone the fan follows, with that fan's name and a link to the song on Spotify. *Done 2026-10-25: the door is
      a card in the feed after the sixth item (or at the end of a shorter feed); "Knock" draws a song inline
      (`BouncerDoorCard`). Verified: Feed tests, clicked through in Chrome at phone size.*
- [x] **Grab bag** screen: pick up to 5 favorite songs (any artist), reorder or remove them. Shared with
      followers; sharing can't be turned off. *Built 2026-10-25 (`/grab-bag`); reordering with up and down arrows that
      swap a song with the slot above or below (`PUT /me/grab-bag/order`). Verified: GrabBagApiTests, GrabBagScreen tests.*
- [x] **Change "Post a photo" to something better** (requested 2026-10-25): *done 2026-10-25 as a round camera button
      floating over the feed (drawn with views; no icon font), hidden while searching. Verified: HomeScreen tests, Chrome.*
- [x] **Use real artists and albums for dev testing** (requested 2026-10-25): *done 2026-10-25: with Spotify credentials
      `tools/dev/seed.py` fetches real artists (`REAL_ARTISTS`), their newest albums, covers, and tracks with the app
      token; grab bags use real track ids. Without credentials (CI, or `--fake`) it falls back to made-up artists.
      Verified with a faked Spotify (`tools/ci/test_seed_catalog.py`) and both variants applied to a fresh database;
      not yet run against real Spotify (no credentials in the build environment).*
- [x] **Music trivia** (requested 2026-10-25): *done 2026-10-25 as daily trivia: one question a day per fan, made from
      the stored catalog of one of their scored artists (which album a song is on, or an album's release year), on the
      home screen above the feed. A right answer earns `trivia_correct`, 0.30 points, a tenth of logging an album
      (decided 2026-10-25: "a tenth of other categories"), once a day. Verified: TriviaApiTests, invariants,
      DailyTriviaCard tests, answered in Chrome.*
- [x] **Music prompts** (requested 2026-10-25): *done 2026-10-25 (migration 0025): one prompt a day for everyone, from
      staff (Profile, "Daily prompts") or a built-in library of 30 (`prompt_library`), stored in `prompts`; one answer
      per fan (`prompt_answers`), public or followers-only, editable on the day. Home shows the prompt card; once
      answered, up to three answers from people the fan follows and "See all answers". No points. Verified:
      PromptApiTests 3, invariants, Prompts tests, Feed and Moderation tests.*
- [x] **Trivia results** (decided 2026-10-25): once a fan answers, the card names one or two people they follow who
      got it right and the percent of fans who did. Fans of an artist share the day's question (migration 0024) so
      this works. *Done 2026-10-25. Verified: TriviaApiTests (shared question and results), DailyTriviaCard tests.*
- [x] **Share prompt answers on the feed** (requested 2026-10-25): *done 2026-10-25: answers from the last week are a
      feed item (everyone's public ones; followers-only ones to followers), with Follow and Report; staff take them down
      from the reported-posts queue.*
- [x] **Featured artist of the week** (requested 2026-10-25): *done 2026-10-25: staff pick it (decided) with a video
      (an uploaded MP4, or a YouTube or TikTok link) and a Spotify song; it shows above the artist list on home. The web
      build plays the video in place; phones open it in YouTube, TikTok, or the browser (in-app playback needs
      `expo-video`, not installed yet). Staff screen: Profile, "Featured artist". Verified: FeaturedApiTests (link,
      upload, validation), Featured tests, Chrome.*
- [ ] **Blind album rankings** (requested 2026-10-25; not daily, decided 2026-10-27: released on a day without trivia
      or a prompt, so never Tuesday or Thursday; the exact day and how often are open): a game where fans rank albums without seeing which
      artist or title they are (for example by cover art or a short clip), then see the reveal and how their ranking
      compares. Open: what is shown blind (covers alone would give it away for big albums; 30-second previews are no
      longer available to new Spotify apps), whether it earns points, and which albums (the fan's artists or anyone's).
- [ ] **"This or that" band game** (requested 2026-10-28): pick between two bands (or songs, albums), released on a
      day without trivia, blind rankings, or a prompt. Open: the day, what's compared, points, and the results shown.
- [x] Sign-up: choose a primary hub city (required), plus up to two backups.
- [x] Changing hub cities: allowed once a week; warn before saving (next change possible in 7 days, and hub
      cities decide which allocations the fan qualifies for).
- [x] Fan pages (2026-10-25): `/fans/{handle}` from any fan's name; Follow, band links, fandom stats and grab bag for
      followers, recent logs and prompt answers. Verified: FanProfileScreen tests, Chrome.
- [ ] Profile: followers and following lists, remove a follower, block someone. *Lists and remove built 2026-10-25
      (`/people`); blocking not wanted for now (decided 2026-10-24).*
- [ ] Respect reduced-motion settings: the bounce and horizontal scroll need a calmer fallback, and the feed
      must work with VoiceOver and TalkBack.

## Ticket allocations (Phase 11)
- [ ] Show upcoming allocations for followed artists and the fan's eligibility, with what to do to qualify.
- [ ] Register for an allocation; show status (registered, selected, not selected, expired).
- [ ] Claim a reservation and hand off to checkout; show the non-transferable ticket or delivery date.
- [ ] Face-value exchange or return flow.
- [ ] Release builds via EAS Build; beta via TestFlight and Google Play closed testing. *`eas.json` profiles written
      2026-10-29 (`development`, `development-simulator`, `beta`, `production`); no build run yet (needs `eas init`).*

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
