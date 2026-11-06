# TODO — Background workers

Use `- [x]` only after the task is implemented and verified. Audited 2026-11-06 against the code.

## Implementation checklist
- [x] Confirm the queue technology based on the chosen backend stack. (Postgres queue, [ADR 0004](../docs/adr/0004-postgres-job-queue.md))
- [x] Define job naming, payload validation, idempotency keys, retry policies, and failure handling. (ADR 0004; payload validation is per handler)
- [x] Ensure long-running sync, onboarding, scoring, OCR, verification, redaction, and notification work runs outside
      request handlers. *Every kind has a handler here (`README.md`, "Layout"): Spotify, Apple Music, and Tidal syncs,
      artist onboarding and catalog refresh, scoring, allocation draws, image redaction, storage cleanup, account
      purge and retention, fraud detection, concert syncs, and notifications (push, presales, delivered tickets,
      staff email). Verification runs in the API's submission flow only as far as it exists (status transitions and
      staff review); there is no OCR job because no OCR exists yet (`services/api/src/integrations/ocr/TODO.md`,
      an open decision). The API never does this work in a request: it enqueues (`enqueue_job()`) and returns.*
- [x] Add operational visibility for retries, stuck jobs, and permanent failures. *A job that fails its last attempt
      logs `failed permanently` at Error and stays in `jobs` with status `failed`; on GCP a log-based metric and
      alert watch for it, the dashboard shows queue depth and age, and the liveness endpoint (`GET /healthz`,
      `hosting/`) fails the pod when a loop stops reporting (`infrastructure/RUNBOOK.md`, 2026-10-25). Retries are
      the `attempt` column and the `Job ... failed, retrying` log line.*
- [x] Ensure jobs re-check current artist/submission/user status before applying side effects. *The rule is in
      `README.md` ("Adding a job", step 1) and each handler loads what it needs at run time instead of trusting the
      payload: e.g. `PushNotificationJob` and `StaffEmailJob` re-read the account (deleted, settings) and the notice
      (read, stale); `SpotifySyncJob` skips a disconnected or deleted fan; `RedactSubmissionJob` skips a submission
      no longer verified; `SelectAllocationJob` re-checks the allocation's state under a lock. Verified in each job's
      tests (`*JobTests`, `PushNotificationTests`, `StaffEmailTests`).*
- [x] Document graceful shutdown and concurrency limits.
- [x] Add tests for duplicate delivery, retries, and partial failures. (`JobQueueTests`)

## Verification
- [x] Add or update automated tests. *One test class per job family in `tests/FandomScore.Tests`
      (`JobQueueTests`, `ScoringJobTests`, `ConcertSyncJobTests`, `MusicSyncJobTests`, `RedactionJobTests`,
      `StorageCleanupJobTests`, `GrabBagRefreshJobTests`, `ArtistOnboardingJobTests`, `PushNotificationTests`,
      `StaffEmailTests`, `RetentionSwitchTests`, `AccountDeletionTests`), each running the handler against a
      migrated test database.*
- [x] Run the relevant tests and record actual results. *Recorded per feature in `PROJECT_TODO.md`; CI runs the suite
      on every push to `main`. Last full local run: 2026-11-06.*
- [x] Update the corresponding README and root project checklist. *`README.md` here lists every job and its
      settings; Phase 12 and the feature phases in `PROJECT_TODO.md` carry the worker milestones.*
