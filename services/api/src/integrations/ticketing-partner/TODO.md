# TODO — Ticketing partner integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Compare suitable partners and confirm affiliate-program and event-data access.
- [ ] Verify whether partner purchase confirmation/webhooks are available and what evidence they provide.
- [ ] Document API credentials, rate limits, attribution rules, and callback security.
- [ ] Implement event lookup and outbound link normalization for the selected partner.
- [ ] Verify callback signatures and reject forged or replayed purchase confirmations.
- [ ] Make partner callbacks idempotent and connect confirmed purchases to the verification pipeline.
- [ ] Add mocked integration tests and a documented manual verification checklist.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
