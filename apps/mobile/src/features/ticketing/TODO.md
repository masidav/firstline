# TODO — Ticketing and merch mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Display available event/ticket links and artist website/merch links.
- [ ] Open external links safely and show a fallback when no link exists.
- [ ] Record outbound clicks through the API without blocking navigation.
- [ ] Clearly distinguish an outbound click from a confirmed purchase.
- [ ] Handle unsupported or invalid URLs gracefully.
- [ ] Add tests for link display, click tracking, and failure fallback.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
