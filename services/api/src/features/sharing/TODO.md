# TODO — Sharing and score cards API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define the score-card payload and ensure it contains only shareable information.
- [ ] Generate or request hosted score-card images using a documented rendering approach.
- [ ] Ensure score cards reflect the current score and estimated-percentile label.
- [ ] Implement secure, non-guessable public share URLs if hosted sharing is used.
- [ ] Define expiration/revocation behavior if share links can be disabled.
- [ ] Track share-card creation and relevant outbound activity without logging unnecessary personal data.
- [ ] Add tests for payload correctness, privacy, link access, and estimated labels.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
