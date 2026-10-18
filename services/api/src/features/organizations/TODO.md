# TODO — Promoter and artist organizations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Add organizations (promoter, artist/management) with members and roles (owner, admin, analyst).
- [ ] Link organizations to the tracked artists and events they control; verify that control before granting access.
- [ ] Enforce organization-scoped authorization on every promoter endpoint.
- [ ] Audit-log membership and permission changes.
- [ ] Test cross-organization access is denied.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
