# Background workers

`FandomScore.Worker` (this folder) is the `worker` process: it runs jobs from the Postgres queue.
Design: [ADR 0004](../docs/adr/0004-postgres-job-queue.md). Table and functions: `database/migrations/0004_jobs_and_retention.sql`.

```bash
dotnet run --project workers        # needs ConnectionStrings__Database, like the API
```

## Layout
| Path | Contents |
| --- | --- |
| `Program.cs` | Host setup and the list of registered job handlers. |
| `jobs/` | Queue runtime: `JobQueue` (claim, finish, retry, schedules), `JobRunner` (polling loops), `IJobHandler`, `PruneJobsJob`. |
| `hosting/` | Secrets mounted as files (`SecretsDirectory`), and the liveness endpoint (`WorkerHeartbeat`, `WorkerHealthServer`). |
| `account-retention/` | `PurgeDeletedAccountsJob`: hourly, purges content of accounts deleted 30+ days ago. |
| `storage-cleanup/` | `StorageCleanupJob` (`storage.cleanup`, hourly): deletes stored files nothing needs (expired upload slots, deleted accounts' files before their purge, files queued in `storage_deletions`). See the folder's README. |
| `concerts/` | `ConcertSyncJob` (`concerts.sync_artist`) and hourly `ScheduleConcertSyncsJob` (`concerts.schedule`): load tracked artists' upcoming shows, sales, and presales from Ticketmaster. See the folder's README. |
| `fans/` | `FanScoresJob` (`fans.overall_scores`, hourly): refreshes every fan's trust and fan level (migration 0039; `services/api/src/features/fan-levels/README.md`). |
| `image-redaction/` | `RedactSubmissionJob` (`submissions.redact`) and hourly `RedactionSweepJob` (`redaction.sweep`): black out barcodes and QR codes in verified proof and move it to the archive. See the folder's README. |
| `spotify-sync/` | `SpotifySyncJob` (`spotify.sync`) and hourly `ScheduleSpotifySyncsJob`. See [ADR 0005](../docs/adr/0005-spotify-integration.md). |
| `allocations/` | `SelectAllocationJob` (`allocations.select`): runs an allocation's draw once staff queue it (`POST /v1/admin/allocations/{id}/selection`). |
| `grab-bag/` | `GrabBagRefreshJob` (`grab_bag.refresh_songs`, daily): keeps grab-bag song titles and covers current from Spotify. See the folder's README. |
| `artist-onboarding/` | `ArtistOnboardingJob` (`artist.onboard`), `CatalogRefreshJob` (`catalog.refresh`) and its hourly scheduler, `CatalogLoader`. See [ADR 0006](../docs/adr/0006-album-catalog.md) and the folder's README. |
| `scoring/`, ... | Planned job handlers (see each `TODO.md`). |

## Adding a job
1. Write an `IJobHandler` in the feature's folder: a `Kind` like `artists.onboard`, and `Every` if it is recurring.
   Make it idempotent and re-check current state (artist status, user deleted) before acting.
2. Register it in `Program.cs`: `builder.Services.AddSingleton<IJobHandler, MyJob>();`.
3. Enqueue it where the work arises, inside that transaction: `SELECT enqueue_job('artists.onboard', '{"artist_id": "..."}', 'artists.onboard:<id>')`.
4. Test it in `tests/FandomScore.Tests` with `JobRunner.RunOnceAsync` against the test database.

## Configuration (`Jobs` section, all optional)
| Setting | Default |
| --- | --- |
| `Jobs__PollInterval` | `00:00:02` |
| `Jobs__Lease` | `00:05:00` (jobs must finish or be idempotent within this) |
| `Jobs__Concurrency` | `2` jobs at a time per process |
| `Jobs__KeepSucceeded` / `Jobs__KeepFailed` | `7.00:00:00` / `30.00:00:00` |
| `Jobs__OnlyKinds` | empty = every kind. Comma-separated kinds this process runs; startup fails on a kind with no handler |
| `Jobs__ExceptKinds` | empty. Comma-separated kinds this process never claims (set one of the two, not both) |

The kind filters let one image run as several Deployments with different identities. In the cluster the
`redaction` Deployment runs `submissions.redact,redaction.sweep,storage.cleanup` (the jobs that touch stored files)
and `worker` excludes them (`infrastructure/k8s/base`). Claims skip other kinds and only the kinds a process runs
get their schedules registered there; due schedules are fired by whichever process polls first. Locally neither is
set and one worker runs everything.

## Liveness (`Health` section)
The worker serves no traffic, so Kubernetes cannot tell a stuck process from a quiet one. Each job loop reports to
`WorkerHeartbeat` every iteration; with `Health__Port` set (the container image sets `8081`), `GET /healthz` answers
`200 {"status":"ok", ...}` while every loop has reported within `Health__StaleAfter` (default 10 minutes, twice the
lease) and `503 {"status":"stale", ...}` otherwise, which fails the pod's liveness probe
(`infrastructure/k8s/base/worker-deployment.yaml`). A loop that cannot reach the database keeps reporting: that is
logged (`Job loop error`) and alerted on, since a restart would not help. Locally the port is unset and nothing listens.

A job that fails on its last attempt is logged at Error (`failed permanently`) and stays in the `jobs` table with
status `failed`; on GCP a log-based metric and alert watch for that line (`infrastructure/RUNBOOK.md`).

## Storage (`Storage` section)
The worker reads and deletes stored files (redaction, `storage.cleanup`), so it needs the same storage as the API:
`Storage__LocalPath` (default
`.local-storage` under the worker's content root) must name the API's directory. Locally, set it to the absolute
path of `services/api/.local-storage` for the worker. `packages/storage/README.md`.

## Operating
- Inspect: `SELECT kind, status, attempts, last_error, run_at FROM jobs WHERE status <> 'succeeded' ORDER BY run_at;`
- Retry a failed job: `UPDATE jobs SET status = 'queued', finished_at = NULL, attempts = 0, run_at = now() WHERE id = ...;`
- Any number of worker replicas can run; claims never overlap.

## Tests
`tests/FandomScore.Tests/JobQueueTests.cs`: success, dedupe, scheduled runs, retries with backoff, permanent
failure, unknown kinds, crashed-worker recovery, concurrent claims, and the deleted-account purge.
`StorageCleanupJobTests.cs`: storage cleanup against a temporary directory. `RedactionJobTests.cs`: redaction with
synthetic tickets.
