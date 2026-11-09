# TODO — Redaction and archive worker

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Accept only verified submissions that weren't taken down. (`RedactSubmissionJob`; RedactionJobTests.)
- [x] Locate and redact ticket barcodes and QR codes with a documented method (ZXing.Net over sections and rotations,
      padded black boxes, rescans; `README.md`); flag uncertain cases for staff (`needs_review`). Verified 2026-10-26:
      RedactionJobTests (5 code types incl. a tilted barcode and off-centre Aztec/Data Matrix).
- [x] Verify that redaction completed (a final scan finds nothing) and the redacted copy reads back before the archive
      write.
- [x] Never treat a clean scan as proof: tickets and screenshots with no code found go to staff.
- [x] Move the redacted file to the archive tier and set redacted=true only after those checks, in one transaction
      with the upload slot.
- [x] Archive verified proof within 30 days of upload. Hourly `redaction.sweep` re-queues missed work and warns at 25
      days; Terraform alert `redaction_overdue` emails on that warning (validated, not applied).
- [x] Enforce that unredacted files can't enter the archive (`submission_files_check`, and
      `submission_files_archive_consistent` from migration 0029; invariants).
- [x] Failure handling keeps originals quarantined and private (RedactionJobTests failed-write case).
- [x] Retention: originals are queued for deletion when archived (0027); rejected and taken-down files go after 30
      days (`storage.cleanup`).
- [x] Staff decisions: `GET /v1/admin/redactions`, `POST /v1/admin/redactions/{fileId}/decision` (archive or
      discard), the app's Photo redaction screen, and a `staff_redaction` notice.
- [ ] Hide printed order numbers too (OCR), if staff find them on tickets often. *Built 2026-11-06 behind a switch
      (migration 0061, `TextFinder.cs`, `OrderNumberFinder.cs`, README "Printed order numbers"): with
      `Redaction__TesseractCommand` set, tickets, screenshots, and receipts have their order and confirmation numbers
      blacked out, counted (`text_redacted`, `extracted` counts only), and hashed into `order_ref_hash`; a number
      another fan already verified sends the file to staff. Verified: RedactionJobTests (finder rules, TSV parsing, a
      fake Tesseract end to end, the duplicate case, OCR off and failing). Decided 2026-11-06: Tesseract is in the
      worker image (`Dockerfile` target `worker`, no longer chiseled; checked once on a synthetic ticket in the built
      image). Still open: the rules haven't met a real ticket. Whether staff find printed numbers often is measurable:
      count archived files with `staff_boxes` (query in `services/api/src/features/redaction/README.md`).*
- [x] Let staff draw boxes themselves on a file the scanner can't settle. *Done 2026-11-05 (migration 0056,
      `StaffBox.cs`): the archive decision carries up to 20 boxes as fractions of the image; the job paints them
      black before scanning; the app's Photo redaction screen has a "Draw boxes" mode (drag with mouse or finger).
      Verified: RedactionJobTests 9 (2 new: pixel checks inside and outside the boxes, pending files ignore boxes),
      RedactionApiTests 2 (1 new: validation and storage), invariants for 0056, Jest 8 in
      `apps/mobile/src/features/redaction` (geometry in `boxes.test.ts`, drawing and the request in
      `RedactionScreen.test.tsx`).*
- [ ] Run redaction in its own `redaction` deployment once the Cloud Storage file store exists (only it may read
      quarantine; ADR 0002).
- [ ] Try the scanner on real ticket photos (phone shots, glare, angles) and tune the passes. David is adding a set
      of real photos (2026-10-26); keep them out of the repo since they hold real order details.

## Verification
- [x] Automated tests: `RedactionJobTests.cs` (7), `RedactionApiTests.cs` (1), invariants for 0029, the proof feed
      test runs the real job. `dotnet test` 266/266 on 2026-10-26. 2026-11-05: RedactionJobTests 9, RedactionApiTests
      2, invariants for 0056.
- [x] Worker image: built with `docker build --target worker` and run against a scratch database on 2026-10-26; it
      archived a synthetic ticket with its QR code blacked out (SkiaSharp's native library loads in the chiseled
      image).
- [x] README with interfaces, behavior, and limits.
