# Proof of fandom: submissions

Fans upload a photo (`POST /uploads`, then `PUT` the bytes to the signed link; metadata stripped on the server) and
submit it as proof of one kind; staff review it; approval awards points (`<kind>_verified` in `scoring_rules`, by
trigger) and queues redaction and archiving (`workers/image-redaction`). Migrations 0001, 0012, 0027 to 0029, 0056, 0061.

| File | What |
| --- | --- |
| `UploadEndpoints.cs` | `POST /uploads`, the signed `PUT`, `GET /files/{id}` (signed, expiring links). |
| `ImageMetadata.cs` | Strips EXIF/XMP/text chunks from JPEG and PNG before anything is stored. |
| `SubmissionEndpoints.cs` | `POST /submissions`, `GET /me/submissions`, `GET`/`DELETE /submissions/{id}`, `PUT /submissions/{id}/share`; staff: `GET /admin/reviews`, `GET /admin/submissions/{id}`, `POST .../decision`, `PUT .../shareable`. |

## Kinds
| Kind | Proof of | Points (provisional) | On the feed |
| --- | --- | --- | --- |
| `ticket` | a ticket | 50 | never (barcodes, personal details) |
| `screenshot` | a streaming or purchase screenshot | 5 | never |
| `receipt` (2026-11-06) | an order confirmation or receipt | 20 (as merch) | never |
| `merch` | merch the fan owns | 20 | shareable once verified |
| `concert_photo` | a photo from the show (pinned to a listed show or a typed-in concert) | 5 | shareable once verified |

Receipts exist so a receipt isn't sent as "merch" and shared. When one is anyway, staff mark the submission **not
shareable** (`PUT /admin/submissions/{id}/shareable` with `shareable: false` and a note): it comes off the feed at
once (`share_blocked_at`, trigger clears `shared_at`), can't be shared again (share guard), and keeps its status and
points; the fan sees `share_blocked` and a reason on their post. Staff can lift it. Both options from the 2026-10-26
request are built; which to keep is an owner decision (`docs/OWNER_TODO.md` C).

## Checks staff see (`verification_checks`)
- `manual`: every approve/reject, with the reviewer.
- `file_hash` (2026-11-06): the same photo (same bytes) another fan already sent and that wasn't rejected: the new
  submission is accepted but carries a failed check with how many other fans and submissions have it. A fan's own
  duplicate is refused outright (`409 duplicate_submission`).
- `ocr_extract` and `order_ref` (2026-11-06): written by the redaction worker when OCR is configured (counts only; a
  printed order number another verified submission carries sends the file to staff). See the worker README.

## Limits
- Every submission goes to `needs_review`; there is no automatic verification or trust score yet (the columns exist).
- Tests: `tests/FandomScore.Tests/ProofApiTests.cs`, `RedactionJobTests.cs`, invariants for 0012, 0029, 0056, 0061.
