# Redaction decisions (staff)

`RedactionEndpoints.cs`: what staff do with verified proof the redaction worker couldn't settle
(`workers/image-redaction`, migration 0029).

| Route | |
| --- | --- |
| `GET /v1/admin/redactions` | Files in `needs_review`, oldest first (max 100), with the worker's note, the fan, the artist, a signed link to the quarantined original, and `deleted_after` (upload + 30 days, when the quarantine bucket deletes it). |
| `POST /v1/admin/redactions/{fileId}/decision` | `{"action": "archive"}`: staff looked; the file becomes `approved` and `submissions.redact` is queued (the worker still blacks out codes it finds, then archives). `{"action": "discard"}`: the file row and its upload slot are deleted and the stored file goes now (or via `storage.cleanup`); the submission keeps its status and points. `404` unknown file, `409 conflict` not waiting for a decision, `422` other actions. |

Staff only (`AuthSetup.StaffPolicy`). Staff hear about new files through the `staff_redaction` notification.
App: `apps/mobile/src/features/redaction/RedactionScreen.tsx` (route `/redactions`, Profile → Photo redaction).
Tests: `tests/FandomScore.Tests/RedactionApiTests.cs`.
