# Data inventory, deletion, retention, and logs

Privacy review of 2026-11-01. What personal data the app keeps, why, who can see it, what account deletion does to
it, and how long it lives. "Fan" is the person the row is about. "Staff" is `users.is_staff` (the admin screens);
"promoter" is an organization member (promoter dashboard). Migrations are in `database/migrations`.

Account deletion (`DELETE /v1/me`) has two steps:
- **At once** (the `users_*` triggers in 0001, 0002, 0010, 0013/0017, 0016, 0018, 0020, 0021, 0026, 0032, 0037, 0052,
  0053, 0054): personal fields are cleared, sessions and sign-in identities end, Spotify and listening data, social links,
  devices, the fan's notices, and the fraud signals (address hashes, device and Spotify history, eligibility checks)
  go, registrations are withdrawn and reservations cancelled.
- **After 30 days** (decided 2026-10-18; `storage.cleanup` then `accounts.purge_deleted`, `purge_deleted_user()`): the
  fan's stored files, then their content (logs, lists, comments, submissions, prompt answers, trivia, scores, share
  cards) are deleted. Until then content shows as "Deleted user".

`tests/FandomScore.Tests/AccountDeletionTests.cs` seeds a fan with data in every area, deletes and purges the account,
then scans **every column of every table** for the fan's id, handle, email, and words, and fails on anything not
listed under "Kept after the purge" below.

## Tables

| Table | Personal data | Why | Who sees it | On deletion | Over time |
| --- | --- | --- | --- | --- | --- |
| `users` | handle, email, display name, avatar link, staff/artist flags, band links, notification settings | the account | handle, name, picture: everyone; email: the fan | personal fields and flags cleared at once; the row stays (anonymous id, created/deleted times, settings flags) so kept records keep an owner | the row is kept for good |
| `auth_identities` | Apple/Google subject id | sign-in | nobody (not in the export: open question) | deleted at once | while the account lives |
| `auth_sessions`, `refresh_tokens` | device sessions, token hashes | sign-in | the fan (export, without hashes) | deleted at once | **deleted 30 days after expiry or revocation** (`privacy.retention`, 0053); sessions last 180 days |
| `spotify_connections` | Spotify user id, encrypted refresh token, sync state | Spotify sync | the fan | deleted at once (Spotify has no revoke API; the app tells fans to remove access in Spotify) | until disconnect |
| `music_connections` | Apple Music or Tidal: Tidal user id, encrypted token, storefront, sync state (0058) | Apple Music and Tidal sync | the fan | deleted at once (`trg_user_delete_music`); neither provider has a revoke API, the app tells fans to remove access in the service | until disconnect |
| `user_artist_signals`, `user_track_likes` | per tracked artist and service (`provider`, 0058): counts, ranks, likes | the fandom score | the fan; scores derived from them are shown | deleted at once (and again at purge) | replaced every sync; a provider's rows deleted on its disconnect |
| `user_album_saves`, `user_plays` | saved albums; play ledger (tracked artists only) | the fandom score | the fan | **deleted at once** (fixed 0053; and again at purge) | plays older than `Spotify__PlayHistoryRetention` (400 days) pruned by `spotify.poll_plays`; **deleted on disconnect and on switching Spotify accounts** (fixed 2026-11-01) |
| `user_artist_scores`, `share_cards`, `fan_overall_scores` | scores, percentiles, trust/activity parts | eligibility, profile | the fan; percentiles/levels shown to others | overall score **deleted at once** (0053); artist scores and cards at purge | recomputed by the scoring jobs |
| `score_events` | points per source | the ledger | the fan | **kept**, reversed at purge (append-only, decided 2026-10-18) | kept for good |
| `user_hub_cities` | home and backup cities | local-first draws | the fan; staff in draws | deleted at once | changeable weekly |
| `album_logs`, `comments`, `lists`, `list_items` | reviews, ratings, comments | journal | per visibility; staff for reports | purged after 30 days (answered comments become "[deleted]") | until the fan deletes them |
| `prompt_answers`, `trivia_questions` | answers | engagement | per visibility / the fan | purged after 30 days (trivia **added to the purge**, 0053) | kept while the account lives |
| `post_reactions`, `follows`, `grab_bag_songs` | reactions, social graph, favourite songs | social | followers, everyone for reactions counts | deleted at once | until removed |
| `plus_one_declines` | who declined whose +1 | +1 rules | nobody | **deleted at once** (0053) | kept |
| `reports` | the reporter, reason, free-text details | moderation | staff | **kept** (reporter id points at the anonymous row; details stay): open decision | kept for good: open decision |
| `moderation_actions` | staff id, target id, staff note | moderation record | staff | kept (staff work record) | kept for good: open decision |
| `submissions`, `submission_files`, `verification_checks`, `uploads` | proof photos, captions, concert, OCR text (`extracted`), order-ref hash, trust score, reviewer ids | proof of fandom | the fan (outcome), staff (all) | purged after 30 days, after `storage.cleanup` deletes the files | rejected/taken-down files 30 days; unused slots 1 day after expiry; OCR text kept with the submission: open decision |
| `users.avatar_upload_id` + `uploads` (purpose `avatar`) | profile picture | profile | everyone | link cleared at once; **upload row and file deleted at once** (0053; was at purge) | replaced picture deleted at once |
| `allocation_registrations`, `allocation_trust_snapshots`, `allocation_selections` | the standing snapshot used for a draw, hubs, +1, outcome, rank | fan draws, repeatable | the fan; staff; promoters see handles and status | withdrawn, hubs and +1 cleared, the score snapshot **kept** so draws stay repeatable | kept for good: open decision |
| `reservations` | seats, face value, ticket token | tickets | the fan; staff | cancelled, seats passed on; row **kept** | kept for good: open decision (likely a legal/tax record once money moves) |
| `fraud_flags`, `fraud_appeals`, `eligibility_audit` | flags, evidence, staff notes, the fan's appeal text, the audit of every eligibility decision | anti-fraud | staff; the fan sees their hold and appeal | **kept** (ids point at the anonymous row) | kept for good: open decision |
| `notifications` | the fan's notices; staff notices naming a fan (`data.handle`), +1 notices naming the host (`data.host_handle`) | in-app and push notices | the recipient | the fan's own deleted at once; **the fan's handle removed from others' notices** (0053) | kept for good: open decision |
| `push_devices`, `push_deliveries` | Expo push tokens, delivery receipts | push | the fan (devices, without tokens) | deleted at once | **disabled devices and delivery records deleted after 30 days** (`privacy.retention`, 0053) |
| `idempotency_keys` | cached responses of the fan's requests | safe retries | nobody | **deleted at once** (0053) | **24 hours** (the API pruned only the caller's own; now also a daily sweep) |
| `client_address_events` | `sha256(daily salt ‖ client address)` at sign-up and at each registration, with the UTC day and the allocation; never the address (`docs/security/bot-and-shared-device-detection.md`, 0054) | fraud rules: many accounts from one address in a day | nobody (not in the export: hashes mean nothing on their own) | deleted at once | **30 days** (`privacy.retention`); the daily salt (`signal_salts`) is deleted after 2 days, after which the hashes can't be matched to any address |
| `push_device_accounts` | `sha256(push token)` per account that registered it, first/last seen | fraud rules: many accounts on one phone | nobody (not in the export) | deleted at once | **90 days** after last seen (`privacy.retention`) |
| `spotify_account_links` | `sha256(fixed salt ‖ Spotify user id)` per account that connected it, first/last linked | fraud rules: one Spotify account across fan accounts | nobody (not in the export) | deleted at once | **180 days** after last linked (`privacy.retention`) |
| `eligibility_views` | per fan, allocation, and UTC day: how many times they checked their standing, first and last time | fraud rules: bots that never check or poll | nobody (not in the export) | deleted at once | **90 days** (`privacy.retention`) |
| `hub_location_checks` | when the fan chose to share their location (setting hubs, opening a show's standing, registering): the nearest curated city, distances to it and to the nearest hub rounded to 10 km, whether a hub was within 100 km; **never a coordinate** (`docs/security/hub-location-checks.md`, 0057) | trust (hub confirmed) and the location fraud rules | nobody (not in the export: owner decision 2 in the design note) | deleted at once | **90 days** (`privacy.retention`) |
| `organization_members`, `organization_audit`, `organizations.created_by` | promoter roles | promoter dashboard | organization owners, staff | membership removed at once; audit kept | kept: open decision (staff work records) |
| `outbound_clicks` | ticket/merch link clicks | partner reporting | staff (counts) | unlinked from the fan at purge | kept (anonymous after purge) |
| `link_clicks` | none (no user column) | counts | staff | n/a | kept |
| `artists.added_by`, `events.created_by`, `allocations.created_by`, `prompts.created_by`, `featured_artists.created_by`, `allocation_selections.ran_by`, `allocation_complaints.updated_by` | who did it | attribution, the daily artist add limit | staff | kept (id only, points at the anonymous row) | kept: staff work records are an open question |
| `jobs` | user ids in payloads, error text (never secrets) | the queue | operators | not touched | succeeded 7 days, failed 30 days (`jobs.prune`) |
| `storage_deletions` | storage keys only | file cleanup | nobody | n/a | until the file is deleted (hourly) |
| `signal_salts` | random salts for the hashes above (no personal data) | so stored hashes can't be reversed by guessing | nobody | n/a | daily address salts 2 days; the Spotify salt kept |
| catalog and config (`artists`, `albums`, `tracks`, `events`, `cities`, `scoring_rules`, `fraud_rules`, `prompts`, `trivia_daily_questions`, `app_settings`, `job_schedules`, ...) | none | | | | |

### Kept after the purge
What still names a deleted fan after 30 days, all by the anonymous id only (no handle, email, or name is left anywhere;
the test checks it): the `users` row, the ledger (`score_events`, reversed), their draw registrations and reservations,
fraud flags/appeals/audit, reports they filed (with the free-text details and appeal text they wrote), records of
staff work they did, and finished jobs until pruned. Which of these to keep, and for how long, is listed under
"Retention" and in `PROJECT_TODO.md` (Open decisions).

## Stored files

| Files | Bucket / prefix | Who sees them | On deletion | Over time |
| --- | --- | --- | --- | --- |
| Unredacted proof uploads | quarantine (`quarantine/`) | the fan (pending), staff, the redaction workload | deleted by `storage.cleanup` 30 days after deletion | bucket expiry 30 days (decided 2026-10-25); rejected/taken-down 30 days |
| Redacted proof | archive (`archive/`, versioned) | the fan, staff; shared photos on the feed | deleted 30 days after deletion | **old versions deleted 30 days after being replaced or deleted** (Terraform lifecycle rule added 2026-11-01; needs `terraform apply`). Before this, deleted files lived on as old versions |
| Profile pictures | media (`media/avatars/`) | everyone with the link | **deleted at once** (0053) | replaced picture deleted at once |
| Featured videos | media | everyone | stay (staff content, decided 2026-10-25) | kept |
| Data export | not stored | the fan | | |

Cloud Storage soft delete (the bucket default, 7 days) and Cloud SQL backups (14 daily backups on beta, 30 on prod,
7 days of point-in-time recovery) keep deleted data recoverable for that long after it is deleted from the live
system. So a deleted account's content is gone from backups at most ~30 + 30 days after the deletion.

## Logs

Reviewed 2026-11-01: every `LoggerMessage` and logger call in `services/api`, `workers`, and `packages`, the logging
configuration, and what GCP logs at the edge.

- **Application logs** (JSON on stdout, read by Cloud Logging): ids (user, artist, job, file, allocation), counts,
  Spotify/Apple/Ticketmaster status codes and error codes, and fixed messages. No emails, handles, display names,
  tokens, request or response bodies, or Spotify listening data. Exceptions log their message; Npgsql leaves out row
  details unless `Include Error Detail` is set (it isn't anywhere), and jobs store only the exception type and message.
  `Microsoft.AspNetCore` logs at Warning, so request lines (paths, query strings) aren't logged by the API.
- **Fixed:** outgoing HTTP calls were logged at Information by `System.Net.Http.HttpClient` (one line per Spotify,
  Expo, Apple, Ticketmaster request, with the path: fans' Spotify playlist ids, track and album ids). Query strings
  were already redacted by .NET (so the Ticketmaster key never appeared), but the paths were fan data. Both
  `appsettings.json` now log that category at Warning.
- **Load balancer request logs** (GCPBackendPolicy logging; API 100 %, web 10 %): client IP, user agent, the full URL
  (handles in `/users/{handle}` paths, search terms, the short-lived `sig`/`exp` of signed file links), status,
  latency. **VPC flow logs** (10 % sample, metadata) and **NAT** errors. **Cloud SQL** Postgres logs (errors; bound
  parameter values are not logged).
- **Retention:** the Terraform defines no log buckets, sinks, or exclusions, so everything goes to the project's
  `_Default` bucket, which keeps logs **30 days** (Cloud Logging default), and GCP's own admin audit logs to
  `_Required` (400 days, not app data). Nothing exports logs elsewhere.

## Retention

| Data | Period | Status |
| --- | --- | --- |
| Deleted account content | 30 days after deletion | decided 2026-10-18 |
| Quarantine files; rejected and taken-down files | 30 days | decided 2026-10-25 |
| Unused upload slots | 1 day after expiry | decided 2026-10-25 |
| Archive old versions | 30 days after replaced or deleted | implemented (Terraform, to apply) |
| Expired or revoked sessions (and refresh tokens) | 30 days after expiry/revocation | implemented (`privacy.retention`) |
| Idempotency keys | 24 hours | implemented (`privacy.retention` plus the API's own) |
| Push delivery records; disabled push devices | 30 days | implemented (`privacy.retention`) |
| Finished jobs | succeeded 7 days, failed 30 days | already in place (`jobs.prune`) |
| Play ledger | 400 days (`Spotify__PlayHistoryRetention`) | set provisionally, confirm or change |
| Spotify data of a fan whose connection stopped working (`reauth_required`) | kept until reconnect or disconnect | decide (proposal: delete synced data after 180 days without a working connection) |
| Notifications | kept | decide (proposal: read notices after 180 days, all after 1 year) |
| Logs (app, load balancer with IPs, flow logs) | 30 days (Cloud Logging default) | set provisionally, confirm or change |
| IP addresses and rate-limit counters | in memory only (limiter); in logs 30 days | no stored data; follows the log period |
| Fraud signals (0054): address hashes 30 days (daily salt 2 days), push device history 90 days, Spotify account history 180 days, eligibility check counts 90 days | as listed | confirmed 2026-11-02 (`docs/security/bot-and-shared-device-detection.md`); the tables stay out of the fan's data download |
| Hub location checks (0057): nearest city and rounded distances, no coordinates | 90 days | set provisionally 2026-11-05, confirm or change (`docs/security/hub-location-checks.md`, including whether to list them in the data download) |
| Fraud flags, evidence, appeals, eligibility audit | kept, also after deletion | decide (proposal: 2 years after the decision, deleted accounts included) |
| Registrations of closed allocations (standing snapshot, rank) | kept | decide (proposal: clear the score snapshot 1 year after the show; keep outcome and seats) |
| Reservations | kept | decide (likely legal/tax once money moves) |
| Reports filed by a deleted fan (details) | kept | decide (proposal: clear `details` at purge once resolved) |
| Moderation and organization audit, staff work records | kept | decide (proposal: 2 years) |
| OCR text (`submissions.extracted`) | kept with the submission | decide (proposal: clear 30 days after the submission is decided; the order-ref hash still catches duplicates) |
| Outbound clicks of live fans | kept | decide (proposal: unlink after 1 year) |
| Database backups | 14 days beta, 30 days prod, PITR 7 days | set provisionally (Terraform), confirm or change |
