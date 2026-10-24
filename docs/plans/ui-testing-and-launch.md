# UI testing and launch plan

Status: proposed (2026-10-18). Covers the fan mobile app (React Native/Expo) and the promoter web
dashboard. Backend tests are already in place (`tests/FandomScore.Tests`).

## 1. What we test, and with what

| Layer | Fan mobile app | Promoter web dashboard | Runs |
| --- | --- | --- | --- |
| Components and screens | Jest + React Native Testing Library (`jest-expo`) | Vitest + Testing Library | Every pull request |
| End-to-end user journeys | **Maestro** flows on iOS simulator and Android emulator | **Playwright** (Chromium, WebKit, Firefox) | Every pull request (smoke set), nightly (full set) |
| API contract | Responses validated against `openapi.yaml` (shared by both clients) | Same | Every pull request |
| Accessibility | Automated label checks in component tests; manual VoiceOver and TalkBack pass | axe checks in Playwright; keyboard-only pass | PR (automated), each release (manual) |
| Real devices | Device cloud (Maestro Cloud, BrowserStack, or Firebase Test Lab): recent and older iOS and Android phones | Latest two versions of each major browser | Before each release |
| Load | k6 against a seeded local stack (`tools/dev/seed.py --fans 20000`) or beta outside testers' hours: allocation registration and selection at expected peak | Same | Before each pilot on-sale |
| Exploratory | Scripted charters per release (offline mode, slow network, expired session, background/resume) | Same | Each release |

**Why Maestro for mobile:** flows are short YAML files, it works with Expo development builds, it tolerates
timing flakiness better than most tools, and non-engineers can read the flows. Detox is the fallback if we
need gray-box control of the app's internals.

### Critical journeys (must stay green to ship)
1. Sign in (Apple and Google), create a handle, and delete the account.
2. Connect Spotify and see a score appear, marked "estimated" while the baseline dominates.
3. Search for an artist and add it; see the onboarding state, then the active state.
4. Log an album with a rating and review, offline, and see it sync when back online.
5. Submit a ticket photo as proof of fandom and see the status change after staff review.
6. **Allocation:** see an upcoming allocation, check eligibility, register, get selected, claim the
   reservation, and hand off to checkout. Also the not-selected and expired paths.
7. **Promoter:** create an event and allocation pool, open registration, run the draw, export the report.

### Test data and external providers
- UI tests run against a **local dev stack** seeded by `tools/dev/seed.py` (ADR 0002, amended 2026-10-24: there is
  no synthetic staging; beta holds real testers' data and only gets read-only smoke checks).
- Each external provider gets a test path on beta. Spotify and Apple Music use dedicated test accounts.
  Ticketers use their sandboxes. Sign-in can also use a beta-only test identity provider, which is
  compiled out of production builds.
- Every test account and sandbox credential lives in Secret Manager and CI secrets, never in the repo.

## 2. Build and distribution pipeline

- **Builds:** EAS Build produces iOS and Android binaries with one profile per environment
  (development, beta, production).
- **Internal and beta distribution:** TestFlight (iOS) and Google Play internal and closed testing (Android).
- **Over-the-air fixes:** EAS Update ships JavaScript-only fixes without a store review. Native changes
  always need a new store build. Every update gets a rollback channel.
- **Feature flags:** allocation features ship behind server-side flags, so a pilot can be enabled for one
  artist or show and switched off instantly.
- **Promoter web:** deployed to beta on merge and to prod with approval (see ADR 0002).

## 3. Launch stages

| Stage | Who | What's real | Exit criteria |
| --- | --- | --- | --- |
| 0. Internal | Team only, internal builds | Real sign-in and Spotify; no tickets | All critical journeys pass on real devices |
| 1. Closed alpha | ~50 invited fans of 1–2 pilot artists, via TestFlight and Play closed testing | Scores and journal; a **dry-run allocation** with simulated inventory | Crash-free sessions ≥ 99.5%; no fairness or eligibility bugs in the dry run; fraud red-team findings addressed |
| 2. Pilot on-sale | One independent promoter, one show (2,000–10,000 capacity) | **Real face-value allocation**, small share of inventory, manual fallback ready | Allocation completes with no oversell; load test at 3× expected peak passed beforehand; staff on call during the window |
| 3. Public beta | Public listing in both stores, phased release | More artists and promoters, allocations behind flags | Crash-free ≥ 99.5%; support volume manageable; case-study metrics collected |
| 4. General availability | Everyone | Full product | Store ratings, retention, and promoter renewals tracked |

Store releases use Apple's 7-day phased release and Google Play staged rollout (e.g. 5% → 20% → 50% → 100%),
paused automatically if crash or error rates rise.

## 4. Store readiness checklist (verify against current guidelines before submitting)
- [ ] In-app account deletion. Both stores require it, and it's already implemented in the schema.
- [ ] Sign in with Apple, or another equivalent privacy-focused option, when Google sign-in is offered
      (App Store guideline 4.8).
- [ ] App Store privacy "nutrition labels" and the Google Play Data safety form, matching what we actually
      collect (listening data, photos, identity verification).
- [ ] Payment rules: tickets for live events are normally outside in-app purchase rules. Confirm how a
      fan-side subscription or "guaranteed access" fee is classified before building it.
- [ ] Photo, notification, and camera permission prompts with clear purpose strings.
- [ ] Demo account and review notes for the App Review team (beta-backed, allocation flags on).
- [ ] Support URL, privacy policy, and terms published.
- [ ] Allow several days of review lead time before any date-bound pilot on-sale.

## 5. Monitoring after launch
- Crash and error reporting for mobile and web (Sentry or Firebase Crashlytics), linked to release versions.
- Cloud Monitoring dashboards for API latency, error rate, queue depth, and Cloud SQL load, with on-sale-window alerts.
- A launch runbook per pilot: owner, timeline, go/no-go checklist, kill switch (feature flag), rollback for
  each layer (OTA update, store build, API deploy, database restore), and a communication plan for fans
  and the promoter.

## 6. Order of work
1. Phase 14 basics: git, CI, branch protection. Everything below runs in CI.
2. Scaffold the Expo app with Jest and one Maestro smoke flow; scaffold the promoter web with Playwright.
3. Stand up beta on GKE (ADR 0002); seeded synthetic data is the local dev stack's job.
4. Grow the critical-journey suite feature by feature, as each journey is built.
5. Contract tests and k6 load tests before the closed alpha's dry-run allocation.
6. Store readiness checklist before the closed alpha (TestFlight external testing also gets a review).
