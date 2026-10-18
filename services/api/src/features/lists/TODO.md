# TODO — Album lists API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Implement list creation, listing, update, and deletion with ownership checks.
- [ ] Support list title, description, visibility, and other fields only when defined by product requirements.
- [ ] Implement adding and removing albums from a list.
- [ ] Persist stable ordering and implement reorder operations.
- [ ] Validate that list items reference supported albums and tracked artists where required.
- [ ] Enforce visibility and owner permissions when reading or editing lists.
- [ ] Handle duplicate album items according to a documented rule.
- [ ] Add tests for ownership, ordering, duplicates, visibility, and invalid album references.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
