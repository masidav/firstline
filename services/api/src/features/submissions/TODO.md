# TODO — Submission upload and lifecycle

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define supported submission kinds and required metadata for ticket, merch, and screenshot proof.
- [ ] Implement authenticated upload initiation and validate size, MIME type, extension, and upload ownership.
- [ ] Upload originals to a quarantine storage tier with private access by default.
- [ ] Create submission and file records with explicit pending/processing states.
- [ ] Compute SHA-256 hashes and detect duplicate files.
- [ ] Store order-reference hashes rather than raw order references.
- [ ] Queue OCR, metadata, event-match, partner-confirmation, and other verification checks.
- [ ] Expose submission status and manual-review outcomes to the owning user without exposing reviewer-only data.
- [ ] Transition statuses only through validated, auditable state changes.
- [ ] Award points only when a submission becomes verified and reverse awards if it is later rejected.
- [ ] Ensure the same order cannot be verified for multiple people under the configured uniqueness rule.
- [ ] Add tests for invalid uploads, duplicate files/orders, lifecycle transitions, and award reversals.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.

## Status (2026-10-21, branch `proof-feed`)
Built: `POST /uploads` with signed `PUT` links, metadata stripping (JPEG/PNG), `POST /submissions` (straight to
`needs_review`; concert photos pinned to a show: `event_id` of the band's past show, or a typed-in `concert`),
`GET /me/submissions`, `GET`/`DELETE /submissions/{id}`, `PUT /submissions/{id}/share`, the staff
queue and decisions, and `GET /feed`. Tests: ProofApiTests (6), invariants for 0012.
Not yet: automated checks, `client_extracted`, HEIC/WebP on the server. (Cloud Storage: Phase 12. Avatars: the users
feature reuses `POST /uploads` with `purpose: avatar` since 2026-10-31.)

## Stored-file cleanup (2026-10-25)
- [x] `IFileStore` is in `packages/storage`, shared with the worker.
- [x] Every deleted (or re-keyed) `uploads`/`submission_files` row queues its key in `storage_deletions` (migration
      0027, same transaction). Withdrawing deletes the files at once and dequeues them; if storage fails, the worker
      retries.
- [x] Worker `storage.cleanup` (hourly, `workers/storage-cleanup`): expired unused slots (a day past `expires_at`, not
      attached, not a featured video), all files of accounts deleted 30+ days ago (unblocks `purge_deleted_user`),
      and queued keys no live row names. Verified: StorageCleanupJobTests (5), ProofApiTests, invariants.
- [ ] Rejected submissions keep their files: retention is an open decision (`PROJECT_TODO.md`).
- [ ] Files stored before migration 0027 whose rows were already deleted are not known to the queue; there is no
      storage listing to find them (local disk only so far, nothing deployed).
