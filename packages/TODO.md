# TODO — Shared packages

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [ ] Only create shared packages where multiple apps/services genuinely reuse the same code.
- [ ] Define shared API/domain types without coupling the mobile app to backend implementation details.
- [ ] Centralize reusable validation schemas where runtime compatibility allows it.
- [ ] Document package ownership, build order, exports, and versioning.
- [ ] Add tests and build checks for each shared package.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
