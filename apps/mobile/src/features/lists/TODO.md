# TODO — Album lists mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Build list browsing, creation, editing, and deletion screens. (`ListsScreen`, `ListScreen`; editing a title or visibility after creation is not in the UI yet, the API supports it. Verified 2026-10-21: `npm test` ListsScreen 2, ListScreen 2.)
- [x] Allow adding/removing albums and reordering list items. ("Add to list" on the album screen; up, down, and remove on the list.)
- [x] Show empty/loading/error states and preserve ordering after refresh. (Order comes from the API's positions.)
- [x] Respect list visibility and show edit controls only to the owner.
- [ ] Add tests for reorder behavior, empty lists, permissions, and failed saves. (Reorder, empty, and permissions covered; failed saves not yet.)

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: `npm run typecheck`, `npm test` 75/75 (branch `phase-6-journal`).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
