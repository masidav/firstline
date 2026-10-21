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
- [ ] After some scrolling, a **bouncer at a door**. Tapping the door shows a real track from the **grab bag**
      of someone the fan follows, with that fan's name and a link to the song on Spotify.
- [ ] **Grab bag** screen: pick up to 5 favorite songs (any artist), reorder or remove them. Shared with
      followers by default.
- [ ] Sign-up: choose a primary hub city (required), plus up to two backups.
- [ ] Profile: followers and following lists, remove a follower, block someone.
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
