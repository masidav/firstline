# TODO — Test strategy

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect existing test runner, fixtures, and continuous-integration checks. *Checked 2026-11-03: xUnit with
      Testcontainers PostgreSQL 16 (`tests/FandomScore.Tests`), Jest/RNTL (fan app), Vitest (promoter-web), Playwright
      and Maestro journeys; CI jobs listed in `docs/DEVELOPMENT.md` (Continuous integration).*
- [x] Define unit, integration, database, and end-to-end test commands. *`CLAUDE.md` and `docs/DEVELOPMENT.md` (Tests;
      the CI table gives each job's local command); e2e: `apps/mobile/e2e/run.sh`, `apps/promoter-web/e2e/run.sh`,
      `apps/mobile/maestro/run.sh`. Checked 2026-11-03.*
- [x] Create isolated test configuration and ensure tests cannot accidentally target production data. *Checked
      2026-11-03: `PostgresFixture` starts a throwaway PostgreSQL 16 container and each test class creates its own
      database from the container's connection string; no test reads `ConnectionStrings__Database` from the
      environment. External providers are fakes (`FakeSpotify`, `TestAuth`).*
- [x] Add fixtures for users, tracked artists, albums, score rules, submissions, and ledger events. *Per-class seed
      SQL in each test class (no shared fixture library), `FakeSpotify`, `TestAuth`; score rules come from the
      migrations; ledger events are made through the real triggers. `tools/dev/seed.py --fake` seeds the e2e databases.
      Checked 2026-11-03.*
- [x] Test authorization and private/follower/public visibility boundaries. *`SecurityRouteTests`,
      `StaffAuthorizationTests`, visibility tests in `JournalApiTests`, `FollowApiTests`, `FeedApiTests`,
      `ProfileApiTests`, `StatsApiTests`, `GrabBagApiTests`, `OrganizationApiTests` (see PROJECT_TODO cross-cutting).
      `dotnet test` 416/416 on 2026-11-03.*
- [x] Test tracked-artist filtering and artist onboarding failure paths. *`SpotifySyncJobTests` (untracked and
      onboarding artists' data dropped), `ArtistOnboardingJobTests` (Spotify outage retries, missing client secret,
      unknown artist paused, delisted releases). 416/416 on 2026-11-03.*
- [x] Test score calculation, baseline blending, caps, reversals, and duplicate award prevention. *`ScoringJobTests`,
      `FanScoresTests`, `invariants.sql` (baseline weight, caps, reversals, `score_events_source_uidx`),
      `DuplicateRequestTests`. 416/416 on 2026-11-03.*
- [x] Test upload validation, duplicate file hashes, verification transitions, redaction, and archive restrictions.
      *`ProofApiTests` (`Uploads_are_checked_and_duplicates_are_refused`, staff review races, withdrawal),
      `RedactionJobTests`, `RedactionApiTests`, `invariants.sql` archive constraints. 416/416 on 2026-11-03.*
- [x] Test offline journal reconciliation and duplicate-sync behavior. *`JournalApiTests` (one `Idempotency-Key`
      replayed and sent several times at once makes one log and one award); the web e2e journey logs an album
      offline and syncs it (`apps/mobile/e2e/journal.spec.ts`). Checked 2026-11-03.*
- [x] Add end-to-end coverage for core user journeys after the app and API are connected. *Web Playwright journeys
      for both apps and Android Maestro journeys, in CI (PROJECT_TODO Phase 13). Checked 2026-11-03.*
- [ ] Record flaky tests and known gaps; do not report unrun tests as passing. *2026-11-03: no flaky-test record
      exists yet; known gaps are listed under PROJECT_TODO Phase 9 ("Complete integration, end-to-end, security, and
      performance testing").*

## UI testing and launch (Phase 13, see docs/plans/ui-testing-and-launch.md)
- [x] Mobile: Jest + React Native Testing Library for components; Maestro flows for critical journeys. *Done; see
      PROJECT_TODO Phase 13 (Jest 55 suites / 348 tests, Maestro 5/5 on Android in CI, 2026-11-01).*
- [x] Promoter web: Playwright end-to-end tests and axe accessibility checks. *Done 2026-10-30; see PROJECT_TODO
      Phase 13.*
- [x] API contract tests that validate responses against `openapi.yaml`. *Done 2026-10-26
      (`tests/FandomScore.Tests/Contract`); every API test's responses are checked. 416/416 on 2026-11-03.*
- [ ] k6 load tests for allocation registration and selection at expected on-sale peaks. *Local runs done
      (`tools/load`: database load test incl. the draw, k6 through HTTP 2026-11-01). Open: the beta run
      (`tools/load/README.md`, "Running it on beta"; owner step).*
- [ ] Device matrix runs (Firebase Test Lab for Android; cloud or physical devices for iOS) before each release.
- [ ] Manual accessibility pass with VoiceOver and TalkBack before each store release.

## Verification
- [x] Add or update automated tests for the behavior above. *See the items above (checked 2026-11-03).*
- [x] Run the relevant tests and record actual results. *2026-11-03: `dotnet build` 0 warnings, `dotnet test`
      416/416.*
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone. *2026-11-03 audit.*
