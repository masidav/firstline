# Repository inventory (Phase 0)

Recorded 2026-10-18 before any implementation work. Update this file when the facts below change.

## What exists

| Path | Contents | Status |
| --- | --- | --- |
| `database/migrations/0001_initial_schema.sql` (was `database/schema.sql`) | Full PostgreSQL schema: 8 enums, 21 tables, 9 functions, 7 triggers, `v_raw_scores` view, seed `scoring_rules` | **Loads cleanly on PostgreSQL 16.15** (verified in a throwaway container). Header says "Postgres 14+"; 14 is untested. |
| `openapi.yaml` | OpenAPI 3.1 contract, 67 operations: auth, me, artists, scores, albums, journal, comments, lists, submissions, sharing, social, ticketing webhook, admin | Spec only. Operations that need tables missing from the schema are tagged `x-requires-schema`. |
| `ARCHITECTURE.md` | Intended boundaries and architectural rules | Matches the schema's design (ledger, tracked-artist rule, quarantine/redaction). |
| `PROJECT_TODO.md`, `TODO_INDEX.md`, `**/TODO.md` | Phase milestones and 52 feature checklists | All unchecked before this inventory. |
| `**/README.md` (non-root) | 18-line generic starter template, identical across features | Placeholder text, no feature-specific content. |
| `README.md` (root) | Describes the folder as a "TODO pack" to merge into a repository | Describes the TODO pack, not the product. |
| `.clinerules` | Instructions for the Cline editor agent | Tool config, not project documentation. |

## What does not exist

- **No application code.** `apps/mobile/src/features/*` and `services/api/src/features/*` contain only README/TODO files. No `package.json`, `pyproject.toml`, lockfile, or entry point exists anywhere.
- **No backend or mobile stack selected.** The spec proposes React Native/Expo and "Node.js or Python"; nothing in the repo settles it. This stays under *Open decisions* in `PROJECT_TODO.md`.
- **No migrations.** `schema.sql` is a single bootstrap file with no migration tool or history.
- **No tests, CI, `.env.example`, Docker/compose files, or deployment config.**
- **No git repository.** The project directory is not under version control.
- **The source product README is missing.** The TODO pack says it was "derived from the supplied Fandom Score README", and `openapi.yaml` refers to a companion `README.md`, but that document is not in the repo. Product requirements can only be inferred from `openapi.yaml`, `schema.sql`, and `ARCHITECTURE.md` until it is added.

## Documentation conflicts

- The root `README.md` describes the TODO pack rather than the product. Replace it once the product README is restored.
- `openapi.yaml` sits at the repo root while its companion schema lives in `database/`. Pick a home once `services/api` exists.
- Feature READMEs are identical templates. Their "Scope note" already says to replace them.

## Schema compared with the API contract

### Tables already flagged by `x-requires-schema`
`auth_identities`, `refresh_tokens` (auth), `push_devices`, `uploads`, `follows`, `reports`.

### API fields and behaviors with no backing column or table (not flagged)
| API surface | Missing from schema |
| --- | --- |
| `Artist.image_url`, `ArtistSearchResult.image_url` | No `artists.image_url`. |
| `POST /admin/artists/{id}/onboarding/retry` | `artist_status` has no `failed` value and there is no column for the onboarding error. |
| `Score.status` (`computing`) | Not stored; must be derived or added. |
| `Event.tickets_available`, outbound ticket URL | `events` has no ticket URL or availability column. |
| `POST /webhooks/ticketing/{partner}` (`partner_order_id`, `sub_id`, refunds) | No purchase-confirmation table. `outbound_clicks` has no URL or sub-id. |
| `DELETE /submissions/{id}` (withdraw) | `submission_status` has no `withdrawn` value. |
| `ShareCard.share_url`, `targets` | Not stored (can be derived). |
| `Idempotency-Key` (24 h) | No idempotency store (Redis may be the intended home). |
| `DecisionRequest.notes` | Fits in `verification_checks.details`. No gap. |
| Phase 9: blocking, notification preferences, moderation actions/takedowns | No tables. |

## Schema behavior and fixes

Covered by `database/tests/invariants.sql` (all passing on PostgreSQL 16.15, 2026-10-18). The same tests fail against the original schema, so they do detect regressions.

**Already worked**
- `track_artist()` returns an existing artist without creating a duplicate, and enforces the per-user daily add limit.
- An artist cannot become `active` without an `artist_baselines` row. Activation stamps `activated_at`.
- Award daily caps (`comment_posted` 5/day). A verified submission awards points, and rejecting it afterwards reverses them.
- `submission_files` refuses `tier = 'archive'` while `redacted = false`.

**Gaps fixed in `schema.sql` (2026-10-18)**

`schema.sql` was edited in place because no database has been deployed and no migration history exists.

1. **Ledger is append-only.** The `score_events_guard` trigger allows only setting `reversed_at` once. A direct `DELETE` or `TRUNCATE` is refused. Deletes that cascade from a deleted user or artist are still allowed; the trigger detects them with `pg_trigger_depth() > 1`. If a future trigger deletes ledger rows itself, it would pass this check too.
2. **Soft-deleting an album log reverses its awards.** New `album_logs_change` trigger.
3. **A review edited to 100+ characters earns `review_written`.** Shortening it below 100 reverses the award. Only one award can be active at a time, so editing back and forth gains nothing.
4. **A re-verified submission earns points again.** The no-double-award rule is now a partial unique index (`score_events_source_uidx`, unreversed rows only). The reversed row stays in history.
5. **Only `active` artists earn points.** Logs created during onboarding earn nothing and are not back-filled.
6. **Replies must be on the parent's log.** This is enforced with a composite foreign key on `(parent_id, album_log_id)`.

A shared `reverse_points()` function now handles reversals for comments, logs, and submissions.

7. **Account deletion frees the handle and email.** Setting `users.deleted_at` triggers `users_delete`, which:
   - clears the handle, email, display name and avatar, and removes staff rights
   - deletes the Spotify connection and synced library data
   - keeps the row, so content and ledger history still have an owner

   Deletion is one-way. `UserRef` in `openapi.yaml` now has a nullable `handle` and a `deleted` flag. Removing content within the retention window is still a worker job.

**Still open**
8. **`v_raw_scores` has no diminishing returns** and ignores `distinct_albums`, `top_artist_rank`, and `recent_plays`. This belongs to the scoring worker (Phase 5).

The API-contract gaps listed above are not addressed yet. Most of them depend on the backend stack and migration tool.

## Suggested next steps

1. Restore the source product README so requirements can be checked against it.
2. Decide the backend stack and job queue (the first item under Open decisions).
3. `git init` and commit the current state as a baseline.
4. Convert `schema.sql` into the first migration, then add the `x-requires-schema` tables and other API-contract gaps in later migrations.
