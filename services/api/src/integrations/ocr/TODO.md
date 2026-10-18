# TODO — OCR and vision integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Choose on-device OCR and/or server-side OCR providers after reviewing platform support and cost.
- [ ] Define the normalized extraction schema for artist, venue, date, and order details.
- [ ] Treat OCR output as an untrusted signal, not proof that a document is genuine.
- [ ] Queue server-side backstop checks only when needed and permitted.
- [ ] Minimize transfer and retention of original sensitive images.
- [ ] Record provider/check versions and errors for auditability.
- [ ] Add tests with synthetic/redacted fixtures, low-quality images, and missing fields.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
