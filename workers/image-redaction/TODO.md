# TODO — Redaction and archive worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Accept only submissions that have reached the approved verified/archive-eligible state.
- [ ] Use a documented method to locate and redact ticket barcodes and QR codes; flag uncertain cases for manual review.
- [ ] Verify that redaction completed and the resulting file is readable before archive transfer.
- [ ] Never treat OCR-only detection as a guarantee that all sensitive codes were removed.
- [ ] Move the redacted file to the archive tier and set redacted=true only after verification.
- [ ] Archive verified proof within 30 days of upload: the quarantine bucket deletes every file older than that
      (decided 2026-10-25). Alert on verified files still in quarantine after 25 days.
- [ ] Enforce the database/storage rule that unredacted files cannot enter archive.
- [ ] Define failure handling that keeps originals quarantined and private.
- [ ] Apply retention and deletion policies to original and redacted objects.
- [ ] Add tests with synthetic fixtures, missed-code scenarios, and failed-transfer scenarios.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
