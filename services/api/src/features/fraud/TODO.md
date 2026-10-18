# TODO — Fan verification and anti-fraud

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define eligibility signals, weights, and minimum account and listening-history age; store them as data like `scoring_rules`.
- [ ] Detect streaming-farm patterns: looped plays, 24/7 listening, sudden spikes before an allocation, many accounts on one device or network.
- [ ] Freeze each fan's eligibility snapshot when an allocation's registration closes so late gaming has no effect.
- [ ] Verify one person per account at registration (phone or ID check) and record only the verification result, not raw documents.
- [ ] Give staff a fraud review queue with the evidence behind each flag; reuse the audit-trail pattern of `verification_checks`.
- [ ] Support appeals with a recorded decision and reviewer.
- [ ] Keep a living document of known gaming strategies and the mitigation for each.
- [ ] Test that flagged fans are excluded, appeals restore eligibility, and snapshots ignore later changes.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
