# TODO — Notifications API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Define supported notification types, triggers, preferences, and delivery channels.
- [ ] Store device push tokens securely and associate them with the correct user/device.
- [ ] Implement register, refresh, and revoke device-token operations.
- [ ] Honor opt-in preferences and platform notification permissions.
- [ ] Queue notifications in background jobs and prevent duplicate sends.
- [ ] Support score changes, relevant artist/tour news, and friend activity only when those features and data sources exist.
- [ ] Handle invalid device tokens and provider delivery failures.
- [ ] Add tests for preferences, deduplication, revocation, and failure handling.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
