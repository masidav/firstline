# TODO — Verification and review API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define a versioned, auditable format for each verification check and its result.
- [ ] Implement or coordinate file-hash, metadata, OCR extraction, event matching, order-reference, partner-confirmation, and manual checks.
- [ ] Combine checks into a configurable trust score; document the combination and missing-signal behavior.
- [ ] Keep the proposed reject/manual/auto-verify thresholds configurable and explicitly unconfirmed until approved.
- [ ] Route low-confidence submissions to rejection, intermediate cases to manual review, and high-confidence cases to auto-verification only when enabled.
- [ ] Implement a staff-only review queue and reviewer authorization.
- [ ] Require reviewer identity for manual decisions and record decision rationale and timestamps.
- [ ] Prevent invalid status transitions and make processing idempotent.
- [ ] Retain audit records for every check and decision.
- [ ] Add tests for threshold boundaries, absent signals, staff permissions, duplicate jobs, and review outcomes.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
