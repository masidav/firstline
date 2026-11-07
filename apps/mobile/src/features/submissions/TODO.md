# TODO — Submissions mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Build submission type selection for ticket, merch, and screenshot proof.
- [ ] Capture/select images and show clear upload/processing status.
- [ ] Request only required device permissions and validate files before upload.
- [ ] Upload securely to the API's quarantine flow.
- [ ] Display pending, processing, needs-review, verified, and rejected states.
- [ ] Explain rejection or review status without exposing sensitive verification internals.
- [ ] Handle interrupted uploads and safe retries.
- [ ] Add tests for permissions, upload failures, and status updates.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.

## Status (2026-10-21, branch `proof-feed`)
Built: Post a photo (`/post`: concert photo, merch, or ticket; artist; for concert photos the show, picked from the
band's past shows or typed in as date, venue, and city; caption; re-encoded to JPEG ≤ 2048 px before
upload), My posts (`/posts`: status, points, share/unshare, withdraw), staff Review queue (`/review`), and the home
feed (`features/feed/Feed.tsx`). Jest: photoUpload, Feed, MyPostsScreen, ReviewScreen. Clicked through in Chrome.
Not yet: camera capture, several photos per post, native device run.

## Status (2026-11-06): receipts and the staff share block
Built: `receipt` in the kind picker (proof only, no caption, never on the feed, like tickets); Posts hides the share
toggle and explains a `share_blocked` post; the review queue marks a merch or concert photo not shareable with a
staff-only note (`PUT /admin/submissions/{id}/shareable`) and allows sharing again; receipts on Stats and in a fan
page's "verified proofs" total. README section added. Verified: `npm run typecheck` clean; `npm test -- --ci` 69
suites, 441 tests (photoUpload, PostPhotoScreen, MyPostsScreen, ReviewScreen, StatsScreen, FanProfileScreen cover
the new behavior). Not clicked through in a browser against the new API.
