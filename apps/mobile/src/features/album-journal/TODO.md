# TODO — Album journal mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Build album search and album detail/logging screens for tracked artists.
- [ ] Support 1–10 ratings, reviews, listen dates, and relisten entries.
- [ ] Allow selecting public, followers-only, or private visibility.
- [ ] Provide edit and delete/soft-delete interactions according to API behavior.
- [ ] Persist offline journal changes locally and show pending-sync status.
- [ ] Reconcile server errors and conflicts without silently losing user entries.
- [ ] Display cached album metadata and graceful missing-artwork states.
- [ ] Add component and offline-sync tests.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
