# TODO — Background workers

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [ ] Confirm the queue technology based on the chosen backend stack.
- [ ] Define job naming, payload validation, idempotency keys, retry policies, and failure handling.
- [ ] Ensure long-running sync, onboarding, scoring, OCR, verification, redaction, and notification work runs outside request handlers.
- [ ] Add operational visibility for retries, stuck jobs, and permanent failures.
- [ ] Ensure jobs re-check current artist/submission/user status before applying side effects.
- [ ] Document graceful shutdown and concurrency limits.
- [ ] Add tests for duplicate delivery, retries, and partial failures.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
