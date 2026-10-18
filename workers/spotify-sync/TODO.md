# TODO — Spotify sync worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define a job payload with user/connection identifiers and an idempotency strategy.
- [ ] Fetch supported Spotify signals with pagination and rate-limit awareness.
- [ ] Filter results to currently tracked artists before persisting track-level data.
- [ ] Aggregate artist-level signals and update sync timestamps/status.
- [ ] Handle token refresh and mark connections requiring user reauthorization.
- [ ] Retry transient errors with bounded backoff; avoid retrying permanent authorization failures indefinitely.
- [ ] Prevent overlapping sync jobs from corrupting data or duplicating records.
- [ ] Record safe metrics and failure reasons.
- [ ] Test filtering, pagination, retries, partial failures, and duplicate job delivery.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
