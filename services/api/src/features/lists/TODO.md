# TODO — Album lists API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Implement list creation, listing, update, and deletion with ownership checks. (`ListEndpoints`; verified 2026-10-21: ListsApiTests 7/7.)
- [x] Support list title, description, visibility, and other fields only when defined by product requirements.
- [x] Implement adding and removing albums from a list.
- [x] Persist stable ordering and implement reorder operations. (1-based contiguous positions; insert at, move to, remove closes the gap.)
- [x] Validate that list items reference supported albums and tracked artists where required. (Catalog albums only; delisted or unknown albums are 404.)
- [x] Enforce visibility and owner permissions when reading or editing lists.
- [x] Handle duplicate album items according to a documented rule. (One album per list; `409 conflict`.)
- [x] Add tests for ownership, ordering, duplicates, visibility, and invalid album references.

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: ListsApiTests 7/7 (branch `phase-6-journal`).
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
