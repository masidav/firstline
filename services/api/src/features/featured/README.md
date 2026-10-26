# Featured artist API

Migration 0023 (decided 2026-10-25): staff pick one tracked artist per week (Monday to Sunday, UTC) with a video and a
song on Spotify; fans see it on top of the home screen.

- `GET /featured`: this week's pick or null. `video.kind` is `upload`, `youtube`, or `tiktok`; `video.url` opens it
  (a signed 6-hour link for an upload), `video.embed_url` plays it in a page, `video.thumbnail_url` for YouTube.
- Staff: `GET /admin/featured` (last four weeks and scheduled), `PUT /admin/featured/{monday}` (create or replace),
  `DELETE /admin/featured/{monday}`. The body takes `video_url` (YouTube watch, youtu.be, or shorts links; TikTok video
  links) or `video_upload_id`, and `spotify_track` (an open.spotify.com/track link or id; title and cover come from
  Spotify with the app token).
- Uploading a video: `POST /uploads` with `purpose: featured_video`, `mime_type: video/mp4`, up to 100 MB (staff only),
  then the signed PUT. Videos are stored as uploaded (no metadata stripping, unlike photos); the file must start like an
  MP4. Locally on disk like photos; real hosting needs the Cloud Storage bucket (Phase 7).

Tests: `tests/FandomScore.Tests/FeaturedApiTests.cs`, `database/tests/invariants.sql` (0023 section).
