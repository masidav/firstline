# TODO — Promoter and artist organizations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Add organizations (promoter, artist/management) with members and roles (owner, admin, analyst).
      *Promoter organizations done 2026-10-27 (migration 0037, README.md); artist and management ones later.*
- [x] Link organizations to the tracked artists and events they control; verify that control before granting access.
      *Shows belong to the organization that created them (or that staff named); every route checks it.*
- [x] Enforce organization-scoped authorization on every promoter endpoint. *`OrganizationAccess` on every
      `/organizations/{id}` route: membership, role, and that the route's show or allocation is the organization's.*
- [x] Audit-log membership and permission changes. *`organization_audit`, written by a trigger, append-only.*
- [x] Test cross-organization access is denied. *OrganizationApiTests (another organization's and staff-managed shows
      are 404); confirmed the test fails when the ownership check is removed.*

## Verification
- [x] Add or update automated tests for the behavior above. *OrganizationApiTests 5, invariants (0037).*
- [x] Run the relevant tests and record actual results. *2026-10-27: see the commit; every response contract-checked.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
