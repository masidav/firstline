# Comments (app)

`CommentsSection.tsx` renders the thread under a log and its composer. It is used by the log screen
(`src/features/album-journal/LogScreen.tsx`, route `/log/[logId]`), which the album screen and the journal link
to with "N comments".

- Loads `GET /logs/{id}/comments` (first 100, oldest first) and shows replies indented under their parent
  (`threaded()`); deleted comments that still have replies show as `[deleted]`.
- Posts with `POST /logs/{id}/comments` and one `Idempotency-Key` per attempt; "Reply" sets `parent_id`.
- Points are mentioned only when the API returns `points_awarded` ("You earned 1 point: comment."); short or
  self comments just say "Posted."
- Edit and delete appear on your own comments only (`state.me.handle`). Reporting is Phase 9.

Tests: `CommentsSection.test.tsx` (thread and tombstones, posting with points, replies, delete, threading order).
