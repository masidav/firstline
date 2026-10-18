# TODO — User profiles

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define the user profile fields and which fields are public, follower-only, or private. (README; no follower-only profile fields yet)
- [x] Implement read and update endpoints with request validation. (`GET`/`PATCH /v1/me`; public `GET /users/{handle}` comes with social features)
- [x] Enforce that users can update only permitted fields on their own profile.
- [ ] Define user identity and profile references consistently across feature APIs.
- [ ] Implement user-facing privacy preferences required by journal and social features.
- [x] Coordinate account deletion with the privacy workflow and dependent records. (`DELETE /v1/me`; content retention still open)
- [ ] Add tests for ownership, field validation, visibility, and deleted/disabled accounts. *Done for `/me` and deletion; visibility waits on public profiles.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. Verified 2026-10-18: `dotnet test` 27/27 pass.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
