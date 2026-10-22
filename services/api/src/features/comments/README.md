# Comments API

openapi tag **Comments**: `GET/POST /logs/{logId}/comments`, `PATCH/DELETE /comments/{commentId}`.
Code: `CommentEndpoints.cs`. Schema: `comments` (0001), awards via `comments_award` / `comments_reverse` triggers.

## Rules
- **Visibility is the log's.** Comments can be read or posted only on logs the caller can see (own, or public).
  A private log's comments are 404 to everyone else.
- **Replies** carry `parent_id`, which must be a live comment on the same log (`422 parent_id` otherwise). The
  database enforces the same-log rule with a composite foreign key.
- **Points** (`comment_posted`, 1 point, 5 per UTC day) come from the trigger: the body must be 20+ characters and the
  log someone else's; paused artists and the daily cap award nothing. The API reports `points_awarded` only.
- **Editing** sets `edited_at` and never creates or changes points. **Deleting** is a soft delete that reverses
  the award; the comment stays as a tombstone (`deleted: true`, no body) while it has live replies.
- **Limits:** 1 to 2,000 characters; 60 posts per hour per user (`429 rate_limited`); `Idempotency-Key` replays
  a repeated post.
- Only the author can edit or delete (403 for others; 404 if they can't see the log). Moderation and reports are
  Phase 9.

## Tests
`tests/FandomScore.Tests/CommentsApiTests.cs`: threads and ordering, points and the daily cap, reply validation,
edit and delete with tombstones and reversal, visibility and ownership, idempotency, and the rate limit.
