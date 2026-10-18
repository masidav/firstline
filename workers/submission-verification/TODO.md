# TODO — Submission verification worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Load only submissions eligible for processing and claim jobs safely.
- [ ] Run enabled hash, metadata, OCR, event-match, order-reference, and partner-confirmation checks.
- [ ] Persist every check result and version for the audit trail.
- [ ] Combine check outputs into the configured trust score.
- [ ] Apply approved configurable thresholds and route ambiguous cases to manual review.
- [ ] Make status changes and score-award effects transactional/idempotent.
- [ ] Do not archive the original file before redaction has completed successfully.
- [ ] Retry transient provider errors without duplicating checks or awards.
- [ ] Expose stuck jobs and failed checks to operational monitoring.
- [ ] Test retries, partial check failures, threshold boundaries, and repeated delivery.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
