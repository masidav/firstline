# TODO — Test strategy

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Inspect existing test runner, fixtures, and continuous-integration checks.
- [ ] Define unit, integration, database, and end-to-end test commands.
- [ ] Create isolated test configuration and ensure tests cannot accidentally target production data.
- [ ] Add fixtures for users, tracked artists, albums, score rules, submissions, and ledger events.
- [ ] Test authorization and private/follower/public visibility boundaries.
- [ ] Test tracked-artist filtering and artist onboarding failure paths.
- [ ] Test score calculation, baseline blending, caps, reversals, and duplicate award prevention.
- [ ] Test upload validation, duplicate file hashes, verification transitions, redaction, and archive restrictions.
- [ ] Test offline journal reconciliation and duplicate-sync behavior.
- [ ] Add end-to-end coverage for core user journeys after the app and API are connected.
- [ ] Record flaky tests and known gaps; do not report unrun tests as passing.

## UI testing and launch (Phase 13, see docs/plans/ui-testing-and-launch.md)
- [ ] Mobile: Jest + React Native Testing Library for components; Maestro flows for critical journeys.
- [ ] Promoter web: Playwright end-to-end tests and axe accessibility checks.
- [ ] API contract tests that validate responses against `openapi.yaml`.
- [ ] k6 load tests for allocation registration and selection at expected on-sale peaks.
- [ ] Device matrix runs (Firebase Test Lab for Android; cloud or physical devices for iOS) before each release.
- [ ] Manual accessibility pass with VoiceOver and TalkBack before each store release.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
