# TODO — OCR and vision integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Choose on-device OCR and/or server-side OCR providers after reviewing platform support and cost. *Still open
      2026-11-05. Nothing in the repo does OCR; `submissions.extracted` and `order_ref_hash` are never written. The
      first use would be the redaction worker hiding printed order numbers (`workers/image-redaction/TODO.md`), which
      needs word positions, so the choice must return bounding boxes (Tesseract does; so do the cloud vision APIs).
      Staff-drawn boxes (migration 0056) cover the gap and measure how often it bites.*
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
