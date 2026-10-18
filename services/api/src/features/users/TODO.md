# TODO — User profiles

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define the user profile fields and which fields are public, follower-only, or private.
- [ ] Implement read and update endpoints with request validation.
- [ ] Enforce that users can update only permitted fields on their own profile.
- [ ] Define user identity and profile references consistently across feature APIs.
- [ ] Implement user-facing privacy preferences required by journal and social features.
- [ ] Coordinate account deletion with the privacy workflow and dependent records.
- [ ] Add tests for ownership, field validation, visibility, and deleted/disabled accounts.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
