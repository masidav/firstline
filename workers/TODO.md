# TODO — Background workers

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Confirm the queue technology based on the chosen backend stack. (Postgres queue, [ADR 0004](../docs/adr/0004-postgres-job-queue.md))
- [x] Define job naming, payload validation, idempotency keys, retry policies, and failure handling. (ADR 0004; payload validation is per handler)
- [ ] Ensure long-running sync, onboarding, scoring, OCR, verification, redaction, and notification work runs outside request handlers.
- [ ] Add operational visibility for retries, stuck jobs, and permanent failures. *Data is in `jobs`; dashboards and alerts in Phase 12.*
- [ ] Ensure jobs re-check current artist/submission/user status before applying side effects.
- [x] Document graceful shutdown and concurrency limits.
- [x] Add tests for duplicate delivery, retries, and partial failures. (`JobQueueTests`)

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
