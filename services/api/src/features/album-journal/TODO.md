# TODO — Album journal API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Implement album search and metadata lookup for tracked artists only.
- [ ] Use the selected catalog provider and comply with its artwork, attribution, and caching terms. (Spotify; contract additions and rules in [ADR 0006](../../../../../docs/adr/0006-album-catalog.md).)
- [ ] Implement create, read, update, and delete/soft-delete behavior for album logs as defined by the product.
- [ ] Validate ratings as integer values from 1 to 10, representing half-star increments.
- [ ] Support optional review text up to the specified limit and validate listen dates.
- [ ] Support relisten flags and multiple diary entries for the same album.
- [ ] Enforce public, followers-only, and private visibility in every relevant query.
- [ ] Prompt for tracked-artist onboarding when an album belongs to an untracked artist.
- [ ] Connect eligible log, relisten, and substantive-review events to the points ledger through idempotent award logic.
- [ ] Design conflict-safe synchronization for offline-created entries.
- [ ] Add tests for validation, ownership, visibility, rewards, duplicate requests, and offline reconciliation.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
