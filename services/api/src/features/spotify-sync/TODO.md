# TODO — Spotify sync domain/API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define endpoints to connect Spotify, request a sync, inspect sync status, and disconnect (`/me/spotify`, `/me/spotify/sync`).
- [x] Validate that the current user owns the Spotify connection being accessed (every route acts on the signed-in user's own row; one Spotify account per user, `409 spotify_in_use`).
- [x] Queue sync jobs rather than holding API requests open while Spotify is queried (`request_spotify_sync()` in the same transaction).
- [x] Persist sync status, timestamps, and safe failure details (`sync_state`, `sync_error` codes, migration 0005).
- [x] Ensure sync processing filters untracked artists before track-level listening records are persisted (worker).
- [x] Make repeated and retried sync jobs idempotent (dedupe key per user; worker replaces the user's rows).
- [x] Handle expired authorization, missing scopes, provider errors, and rate limits.
- [x] Expose clear client-facing status for in-progress, successful, and failed syncs.
- [x] Add API and integration tests for ownership, filtering, retries, and duplicate processing.
- [ ] Recompute the user's scores after a sync (needs the scoring worker, Phase 5).

## Verification
- [x] Add or update automated tests for the behavior above (`SpotifyApiTests`, `SpotifySyncJobTests`).
- [x] Run the relevant tests and record actual results. 2026-10-18: `dotnet test` 67/67 passed.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
