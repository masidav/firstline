# TODO — Fan verification and anti-fraud

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define eligibility signals, weights, and minimum account and listening-history age; store them as data like `scoring_rules`.
- [ ] Detect streaming-farm patterns: looped plays, 24/7 listening, sudden spikes before an allocation, many accounts on one device or network.
      *Play patterns done 2026-10-29 (migration 0043, `workers/fraud/DetectFraudJob.cs`): looped plays, 24/7 listening,
      rapid plays, spikes before fan tickets. "Click everything" done 2026-10-30 (migration 0045): `burst_engagement`
      on likes and album saves. Devices and networks: no data yet.*
- [x] Keep a living document of known gaming strategies and the mitigation for each. *`docs/security/gaming-strategies.md`, 2026-10-29.*
- [ ] Freeze each fan's eligibility snapshot when an allocation's registration closes so late gaming has no effect.
- [ ] Verify one person per account at registration (phone or ID check) and record only the verification result, not raw documents.
- [x] Give staff a fraud review queue with the evidence behind each flag; reuse the audit-trail pattern of `verification_checks`.
      *Done 2026-10-29 (migration 0042): `fraud_flags` with evidence and notes, `/v1/admin/fraud-flags`, an append-only
      `eligibility_audit` of every eligibility decision. Verified: FraudReviewApiTests 5, invariants (0042).*
- [x] Support appeals with a recorded decision and reviewer. *Done 2026-10-29: one appeal per flag, granted (cleared) or
      denied (upheld) by a named staff member, with a response the fan sees.*
- [ ] Test that flagged fans are excluded, appeals restore eligibility, and snapshots ignore later changes.
      *Excluded and restored: done 2026-10-29 (FraudReviewApiTests). Snapshots wait on the frozen eligibility snapshot above.*

## Verification
- [ ] Add or update automated tests for the behavior above. *Detection rules: FraudReviewApiTests (2026-10-30: the burst
      rule fires on a catalog liked in one sitting, not on gradual saves, once across two runs) and `invariants.sql`.*
- [ ] Run the relevant tests and record actual results. *2026-10-30: `dotnet test` FraudReviewApiTests 8/8, DatabaseTests 2/2.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations. *README.md, 2026-10-29.*
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
