# TODO — Artist search mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Build a search screen with debounce to reduce provider/API requests.
- [ ] Show loading, empty, error, and retry states.
- [ ] Display artist identity and tracked/onboarding/active status.
- [ ] Allow adding untracked artists and prevent repeated taps while the request is pending.
- [ ] Display the daily limit message and onboarding progress.
- [ ] Navigate to the artist page when an artist is already tracked.
- [ ] Refresh status when onboarding completes.
- [ ] Add component/screen tests for search, add, limit, and failure cases.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
