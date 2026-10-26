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
- [ ] Hide printed order numbers too (OCR), if staff find them on tickets often.
- [ ] Let staff draw boxes themselves on a file the scanner can't settle (today they can only archive or discard).
- [ ] Run redaction in its own `redaction` deployment once the Cloud Storage file store exists (only it may read
      quarantine; ADR 0002).
- [ ] Try the scanner on real ticket photos (phone shots, glare, angles) once beta has some, and tune the passes.

## Verification
- [x] Automated tests: `RedactionJobTests.cs` (7), `RedactionApiTests.cs` (1), invariants for 0029, the proof feed
      test runs the real job. `dotnet test` 266/266 on 2026-10-26.
- [x] Worker image: built with `docker build --target worker` and run against a scratch database on 2026-10-26; it
      archived a synthetic ticket with its QR code blacked out (SkiaSharp's native library loads in the chiseled
      image).
- [x] README with interfaces, behavior, and limits.
