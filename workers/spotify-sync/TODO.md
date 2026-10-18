# TODO — Spotify sync worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define a job payload with user/connection identifiers and an idempotency strategy (`{"user_id"}`, dedupe key `spotify.sync:<user>`, rows replaced per sync).
- [x] Fetch supported Spotify signals with pagination and rate-limit awareness.
- [x] Filter results to currently tracked artists before persisting track-level data.
- [x] Aggregate artist-level signals and update sync timestamps/status.
- [x] Handle token refresh and mark connections requiring user reauthorization.
- [x] Retry transient errors with bounded backoff; avoid retrying permanent authorization failures indefinitely.
- [x] Prevent overlapping sync jobs from corrupting data or duplicating records (per-user advisory lock, connection row lock).
- [ ] Record safe metrics (synced count, failures by `sync_error`) for monitoring (Phase 12). Failure reasons are recorded as codes.
- [x] Test filtering, pagination, retries, partial failures, and duplicate job delivery.

## Verification
- [x] Add or update automated tests for the behavior above (`SpotifySyncJobTests`).
- [x] Run the relevant tests and record actual results. 2026-10-18: `dotnet test` 67/67 passed.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
