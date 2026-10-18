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
