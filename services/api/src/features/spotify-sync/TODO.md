# TODO — Spotify sync domain/API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define endpoints to connect Spotify, request a sync, inspect sync status, and disconnect.
- [ ] Validate that the current user owns the Spotify connection being accessed.
- [ ] Queue sync jobs rather than holding API requests open while Spotify is queried.
- [ ] Persist sync status, timestamps, and safe failure details.
- [ ] Ensure sync processing filters untracked artists before track-level listening records are persisted.
- [ ] Make repeated and retried sync jobs idempotent.
- [ ] Handle expired authorization, missing scopes, provider errors, and rate limits.
- [ ] Expose clear client-facing status for in-progress, successful, and failed syncs.
- [ ] Add API and integration tests for ownership, filtering, retries, and duplicate processing.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
