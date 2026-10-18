# TODO — Ticketing, merch, and outbound links API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Choose and document the ticketing/affiliate partner before implementing provider-specific code.
- [ ] Implement provider abstraction only if more than one provider is genuinely needed.
- [ ] Retrieve or cache relevant event and purchase links for tracked artists.
- [ ] Expose artist website and merch links where available and permitted.
- [ ] Record outbound clicks with timestamp, destination/provider, and minimal attribution metadata.
- [ ] Do not treat a click as a completed purchase.
- [ ] Implement partner purchase-confirmation callbacks only if supported and verifiable.
- [ ] Validate callback authenticity and make confirmations idempotent.
- [ ] Add tests for safe URLs, click recording, callback verification, and duplicate confirmations.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
