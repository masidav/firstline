# Submissions mobile feature

## Purpose
This folder owns the submissions mobile feature responsibilities for Fandom Score.

## Responsibilities
- Implement the feature's domain behavior and interfaces.
- Validate inputs and enforce authorization at the correct boundary.
- Handle expected failures and avoid duplicate effects on retries.
- Keep implementation and automated tests close to the feature where practical.

## Dependencies
Review the corresponding `TODO.md` and the repository's architecture documentation for upstream dependencies, provider choices, database contracts, and unresolved decisions.

## Development
1. Inspect existing code and tests before changing files.
2. Implement tasks in `TODO.md` in dependency order.
3. Add tests for success, failure, authorization, and edge cases.
4. Run tests and document actual results.
5. Update documentation and the root `PROJECT_TODO.md`.

## What's here
- `PostPhotoScreen` (`/post`): post a concert photo, merch, a ticket, or a receipt for an artist; concert photos are
  pinned to a show. The kinds offered, their help text, and which of them can ever be shared live in `photoUpload.ts`
  (`POST_KINDS`, `SHAREABLE_KINDS`, `neverShared`).
- `MyPostsScreen` (`/posts`): the fan's posts with status and points; share or unshare a verified merch or concert
  photo; withdraw anything not yet verified.
- `ReviewScreen` (`/review`, staff): the queue with approve, reject (with a reason the fan sees), and the share block
  below.
- `photoUpload.ts`: re-encodes the photo, uploads it to the signed link, submits it, and holds the labels and lines
  the screens share. Tests sit next to each file and mock the API at the generated client (`session.api`).

## The staff share block (2026-11-06; the only answer to receipts since 2026-11-07)
- **Receipts**: there is no receipt kind. One was offered in the picker for a day (2026-11-06) and was retired with
  contract 2.0.0 when the owner chose the staff mark below as the one mechanism. A fan who posts an order
  confirmation as merch gets it marked not shareable in review.
- **Share block** (`Submission.share_blocked`): staff found that a merch or concert photo is really a receipt, or shows
  personal details. In the review queue, a submission of a shareable kind has a "Not shareable" action with an
  optional staff-only note (`PUT /admin/submissions/{id}/shareable` with `shareable: false` and `note`); once blocked,
  the queue shows the note (`AdminSubmission.share_blocked_note`) and "Allow sharing again" (`shareable: true`). The
  response is the updated submission and replaces the item in place; approve and reject are unchanged. The fan sees
  no share toggle and the line "Staff marked this as not shareable (it shows a receipt or personal details). It still
  counts as proof." The post keeps its status and points. Only merch and concert photos get the action: the other
  kinds are never shareable regardless.
