# Redaction and archive worker

Verified proof photos are scanned for barcodes and QR codes, the codes are blacked out, and the redacted copy moves
from `quarantine/` to `archive/`. Nothing unredacted is archived, and the quarantine bucket deletes every file after
30 days (decided 2026-10-25), so verified proof has to get through here in time. Migrations 0029 and 0056.

## Jobs
| Kind | When | What |
| --- | --- | --- |
| `submissions.redact` | Queued by the database when a submission becomes verified, by staff archiving a file, and by the sweep. Payload `{"submission_id": ...}`. | `RedactSubmissionJob`: for each file still `pending` or `approved`, paint any staff-drawn boxes (`approved` only), redact, check the copy reads back, write `archive/{file id}.jpg|.png`, and point the file and its upload slot at it in one transaction. The old key is queued for deletion (migration 0027, `storage.cleanup`). |
| `redaction.sweep` | Hourly. | `RedactionSweepJob`: re-queues verified submissions with files still waiting, and logs `Redaction overdue: ...` (a warning) while verified files are in quarantine 25+ days after upload. Terraform `monitoring.tf` alerts on that line. |

## How codes are found (`CodeRedactor`)
- ZXing.Net reads every format it knows: QR, Aztec, Data Matrix, PDF417, MaxiCode, and the 1D barcodes. SkiaSharp
  decodes and draws.
- ZXing reliably finds a code only when it is roughly upright and, for Aztec and Data Matrix, near the middle of the
  area scanned. Each pass therefore scans the whole image, overlapping sections (2×2 and 3×3, and 5×5 and 7×7 for
  Aztec and Data Matrix), and copies turned by ±22°, ±45°, ±68° and 90°. Images are scanned at 1600 px on the long
  side; boxes map back to full size.
- Each code gets a padded black box: QR points are finder-pattern centres, and a 1D code reports only a line through
  its bars, so its box grows above and below (covering digits printed just under it).
- After painting, the image is scanned again, up to 3 passes.
- About 4–7 s per photo on a laptop; the job runs in the background.

## When staff decide (`needs_review`)
The original stays in quarantine and staff get a `staff_redaction` notice when:
- a **ticket or screenshot shows no code at all**: tickets nearly always have one, so the scanner probably missed it
  (blurry, cropped, tiny, at an odd angle);
- codes are **still found** after 3 passes;
- the image **can't be read**, or the redacted copy doesn't read back;
- the stored file is missing.

Staff use the app's Photo redaction screen (`GET /v1/admin/redactions`, `POST .../decision`): **archive** (they looked;
the worker still blacks out what it finds, then archives), **archive with boxes** they drew over what the scanner
missed, or **discard** (the file is deleted; the submission keeps its status and points). Merch and concert photos with
no code are archived without staff.

## Staff-drawn boxes (`StaffBox`, migration 0056)
`submission_files.staff_boxes` holds the boxes from the archive decision as fractions of the image (`{x, y, w, h}`,
0..1 from the top-left corner; at most 20, each inside the image, checked by the database). The job painted them
first, rounded outward to whole pixels and grown by 2 px a side, then scans for codes as usual; the note on the
archived row says how many were painted (`Archived after staff review; 2 staff-drawn boxes blacked out.`). Boxes
count only on a file in `approved`: a pending file never has any, and if one did, a decision staff didn't just make
shouldn't shape the archive. The boxes stay on the row after archiving so we can tell how often staff need them
(`services/api/src/features/redaction/README.md` has the query).

## Guarantees and limits
- Only verified submissions that weren't taken down are archived. Rejected and taken-down files are deleted after 30
  days by `storage.cleanup`.
- The database refuses archive rows that aren't redacted, aren't under `archive/`, or have no `archived_at`
  (`submission_files_check`, `submission_files_archive_consistent`).
- A failed write or crash leaves the original quarantined and `pending`; the archive key is fixed per file, so a retry
  overwrites instead of duplicating, and a copy whose row update failed is deleted.
- **No OCR**: an order number printed elsewhere on a ticket (not under a barcode) is not found by the scanner; staff
  can draw a box over it when the file comes to them, but a ticket whose codes were all found never reaches staff.
  Tickets and screenshots are never shown on the feed; the archive is staff-only proof. (`submissions.extracted` and
  `order_ref_hash` from the initial schema are not written by anything yet: there is no OCR anywhere, so no
  duplicate-order detection by order number either.)
- A scan that finds nothing is not proof of a clean image; that is why tickets and screenshots without a code go to
  staff.
- `submission_files.sha256` stays the original's (duplicate detection compares new uploads with what fans sent).
- Hosting: since 2026-10-27 redaction runs in its own `redaction` Deployment (same worker image, `Jobs__OnlyKinds`),
  the only worker that can reach the quarantine bucket (ADR 0002 "Files"; `infrastructure/k8s/base/redaction-deployment.yaml`).

Tests: `tests/FandomScore.Tests/RedactionJobTests.cs` (synthetic tickets with QR, tilted Code 128, Aztec, Data Matrix
and PDF417; missed-code, unreadable, failed-write, staff-box, and sweep cases), `RedactionApiTests.cs` (staff
decisions, box validation).
