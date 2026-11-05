# Redaction decisions (staff)

`RedactionEndpoints.cs`: what staff do with verified proof the redaction worker couldn't settle
(`workers/image-redaction`, migrations 0029 and 0056).

| Route | |
| --- | --- |
| `GET /v1/admin/redactions` | Files in `needs_review`, oldest first (max 100), with the worker's note, the fan, the artist, a signed link to the quarantined original, and `deleted_after` (upload + 30 days, when the quarantine bucket deletes it). |
| `POST /v1/admin/redactions/{fileId}/decision` | `{"action": "archive", "boxes": [...]}`: staff looked; the file becomes `approved`, the boxes (if any) are stored in `submission_files.staff_boxes`, and `submissions.redact` is queued (the worker paints the boxes black, still blacks out codes it finds, then archives). `{"action": "discard"}`: the file row and its upload slot are deleted and the stored file goes now (or via `storage.cleanup`); the submission keeps its status and points. `404` unknown file, `409 conflict` not waiting for a decision, `422 validation_failed` for other actions or bad boxes. |

## Boxes (migration 0056)
A box is `{"x", "y", "w", "h"}` as fractions of the image from its top-left corner (`RedactionBox` in `openapi.yaml`),
so the worker paints the same area whatever size the screen showed the photo at. `boxes` is optional and goes only
with `archive` (field `boxes` in the 422 otherwise); at most 20; each needs finite x, y ≥ 0 and w, h > 0 with
x + w ≤ 1 and y + h ≤ 1 (field `boxes[i]` or `boxes[i].x`). An empty list stores null. A later decision on the same
file (the worker can send an approved file back when it can't read it) replaces the boxes. The database checks the
same shape (`submission_files_staff_boxes_check`, `redaction_boxes_valid()`), and the worker honours boxes only on a
file in `approved`.

The column keeps the boxes after archiving, so how often staff draw them says whether printed order numbers are common
enough to be worth OCR (the open half of the Phase 7 item in `PROJECT_TODO.md`):
`SELECT count(*) FILTER (WHERE staff_boxes IS NOT NULL), count(*) FROM submission_files WHERE redaction_note LIKE 'Archived after staff review%'`.

Staff only (`AuthSetup.StaffPolicy`). Staff hear about new files through the `staff_redaction` notification.
App: `apps/mobile/src/features/redaction/RedactionScreen.tsx` (route `/redactions`, Profile → Photo redaction):
"Draw boxes" turns the photo into a canvas (mouse or finger drag; `boxes.ts` holds the geometry, tested on its own).
Tests: `tests/FandomScore.Tests/RedactionApiTests.cs`.
