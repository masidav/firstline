# Privacy and account data lifecycle

What a fan can see, take, and delete of their own data. Account deletion lives with the profile
(`DELETE /v1/me`, `../users/MeEndpoints.cs`, the `users_delete` triggers, and the worker's `accounts.purge_deleted` job);
this folder holds "Download my data".

## Download my data (`GET /v1/me/export`)

`DataExportEndpoints.cs`. A signed-in fan gets one JSON file (`Content-Disposition: attachment;
filename=fandom-score-export-YYYY-MM-DD.json`, `Cache-Control: no-store`) with everything the app holds about them,
grouped by area. The app's Profile screen has a "Download my data" button
(`apps/mobile/src/features/privacy`): the web app saves the file; the phone app writes it to the cache and opens the
share sheet.

- **Synchronous.** Every section is one indexed query by user id, all in one read-only `REPEATABLE READ` transaction
  (a consistent snapshot) with the session time zone set to UTC. A busy fan's file is a few hundred kilobytes (the
  play ledger is the largest section). Move it to the job queue if exports ever take seconds.
- **Rate limited:** 3 per user per hour (ASP.NET fixed-window limiter, policy `data-export`), then `429 rate_limited`
  with `Retry-After`. The limiter is in memory, so with several API replicas a fan could get 3 per replica per hour.
- **Deleted accounts** can't export: deletion ends every session, and a token for a deleted account gets `401`.
- **Explicit columns.** Each section names its columns, so a column added later isn't exported (or leaked) until it
  is added here on purpose. When you add a table that holds per-user data, add a section (or a line under "Not
  included") and update `DataExport` in `openapi.yaml`.
- `format_version` (1) changes only when a section is renamed or removed.

### What is included

| Area | Section | Source | Fields |
| --- | --- | --- | --- |
| account | `profile` | `users` | id, handle, email, display name, avatar, staff/artist flags, band links, presale reminders, push settings, hubs changed at, created at |
| | `sign_in_methods` | `auth_identities` | provider, created at, last used at |
| | `sessions` | `auth_sessions` | created, last refreshed, expires, revoked (and why), which one is this one |
| | `hub_cities` | `user_hub_cities` + `cities` | position, city id, name, region, country |
| | `push_devices` | `push_devices` | id, platform, created, last seen, disabled (and why) |
| | `organizations` | `organization_members` + `organizations` | organization id, name, kind, role, added at |
| spotify | `connection` | `spotify_connections` | Spotify user id, scopes, connected/synced times, sync state and error code, counts; null when not connected |
| | `artist_signals` | `user_artist_signals` | every stored signal per tracked artist |
| | `saved_tracks` / `saved_albums` | `user_track_likes`, `user_album_saves` | track/album, artist, source, saved at |
| | `plays` | `user_plays` | played at, track id and title, artist (tracked artists only, as stored) |
| fandom | `scores` | `user_artist_scores` | raw score, percentile, estimated, baseline weight, breakdown, computed at |
| | `points` | `score_events` (the ledger) | every award, reversed or not |
| | `share_cards` | `share_cards` | |
| | `artists_added` | `artists.added_by` | artists the fan started tracking |
| | `outbound_clicks` | `outbound_clicks` | |
| journal | `album_logs` | `album_logs` | all of the fan's logs, including withdrawn (`deleted_at`) and taken down (`removed_at`) |
| | `lists`, `comments` | `lists`, `list_items`, `comments` | (no API writes these yet; exported once they exist) |
| activity | `prompt_answers` | `prompt_answers` + `prompts` | prompt day and text, the answer, visibility, withdrawn/taken down |
| | `trivia` | `trivia_questions` | question, options, the fan's choice; the right answer only once answered |
| | `reactions` | `post_reactions` | reactions the fan gave |
| | `reports_filed` | `reports` (`reporter_id`) | the fan's reports, their reasons and details, and the outcome |
| proof | `submissions` | `submissions` + `submission_files` + `events` | kind, status, caption, concert, rejection reason, timestamps; per file: id, type, size, redaction state, storage tier, a signed link (an hour) |
| | `uploads` | `uploads` | upload records (purpose, type, sizes, times, submission) |
| social | `following`, `followers` | `follows` | the other fan's handle and when (live accounts only) |
| | `grab_bag` | `grab_bag_songs` | |
| tickets | `registrations` | `allocation_registrations` | the snapshot of standing used for the draw, hubs, +1 handle, outcome, rank, seats |
| | `guest_of` | `allocation_registrations.guest_user_id` | shows another fan named the caller as +1 for: show and host handle only |
| | `reservations` | `reservations` | seats, face value, delivery time, returned/cancelled |
| (top level) | `notifications` | `notifications` | fan notices with their data |

### What is not included, and why

| Data | Why |
| --- | --- |
| Spotify refresh token (`refresh_token_enc`), Spotify access tokens (memory only), `plays_cursor` | Secrets / internal sync state. |
| Refresh-token hashes (`refresh_tokens`), session ids, access tokens | Secrets: anyone holding them could act as the fan. |
| Ticket token (`reservations.token`) | It is the ticket; it shows in the app at delivery time, and a file is easy to forward. |
| Push tokens (`push_devices.token`), Expo delivery records (`push_deliveries`) | A push token lets anyone message the phone; delivery records are operational logs. |
| Sign-in provider subject (`auth_identities.subject`) | Apple's/Google's internal id for the fan's account; no use outside the app. *Product owner may decide to include it.* |
| `submissions.order_ref_hash`, `submission_files.sha256`, `uploads.sha256`/`declared_sha256`, `storage_key`s | Hashes used for duplicate and fraud checks, and storage internals. |
| `submissions.trust_score`, `submissions.extracted` (OCR output), `verification_checks` (and reviewer ids), `submission_files.redaction_note`/`codes_redacted` | Staff-internal verification and fraud signals; showing them would teach people to game verification. The outcome (status, rejection reason, redaction state) is included. *Open: whether the redacted OCR text should be included.* |
| Reports about the fan, `moderation_actions` (staff notes) | Would identify reporters and expose staff notes. Takedowns of the fan's own posts show as `removed_at`. |
| Staff notices (`notifications.kind = staff_*`) | Work items about other fans' posts and shows, not about the caller. |
| Other fans' data | Only handles appear (follows, +1 host/guest, nothing else); never their email, scores, private posts, or standing. Reactions and reports name the target post id, not its author. |
| Staff work records (`events.created_by`, `allocations.created_by`, `allocation_selections.ran_by`, `prompts.created_by`, `featured_artists.created_by`, `organization_audit`) | Records of work done for the business, not data about the person as a fan. *Open for staff accounts.* |
| `idempotency_keys`, `jobs`, `storage_deletions` | Short-lived replay cache (responses the fan already got) and job/cleanup queues. |
| The files themselves | The export is JSON; each proof file has a signed link (an hour) instead. |
| Deleted-account fields (`deleted_at`, `content_purged_at`) | Always empty for an account that can sign in. |

## Tests
- API: `tests/FandomScore.Tests/DataExportApiTests.cs` (seeds a fan with data in every area plus other fans; checks
  their own data, no other fan's private data, no tokens/hashes/storage keys in the raw text, the open trivia answer,
  the download headers, the hourly limit, and that a deleted account can't export). Set `DATA_EXPORT_PREVIEW=<path>`
  to write the seeded fan's file.
- App: `apps/mobile/src/features/privacy/downloadMyData.test.ts` (web and phone), `ProfileScreen.test.tsx` (button).
