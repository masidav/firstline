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
| `hosting/` | Secrets mounted as files (`SecretsDirectory`), the liveness endpoint (`WorkerHeartbeat`, `WorkerHealthServer`), and the integration probes behind `GET /healthz/integrations` (`IIntegrationCheck`). |
| `account-retention/` | `PurgeDeletedAccountsJob`: hourly, purges content of accounts deleted 30+ days ago. `PruneExpiredRecordsJob` (`privacy.retention`): daily, deletes sessions expired or revoked 30+ days ago, idempotency keys after 24 hours, and push and email delivery records and disabled push devices after 30 days (migrations 0053, 0059), and the fraud rules' client signals past their period (address hashes 30 days, push device history 90, Spotify account history 180, eligibility checks 90; migration 0054). The same job prunes the records whose period is still an owner decision, each only once its `Retention__*` setting is set (`RetentionOptions`, migration 0060; see "Retention" below). |
| `storage-cleanup/` | `StorageCleanupJob` (`storage.cleanup`, hourly): deletes stored files nothing needs (expired upload slots, deleted accounts' files before their purge, files queued in `storage_deletions`). See the folder's README. |
| `concerts/` | `ConcertSyncJob` (`concerts.sync_artist`) and hourly `ScheduleConcertSyncsJob` (`concerts.schedule`): load tracked artists' upcoming shows, sales, and presales from Ticketmaster. See the folder's README. |
| `fans/` | `FanScoresJob` (`fans.overall_scores`, hourly): refreshes every fan's trust and fan level (migration 0039; `services/api/src/features/fan-levels/README.md`). |
| `fraud/` | `DetectFraudJob` (`fraud.detect`, every 6 hours): runs the fraud rules over fans who played, saved, signed up, registered, checked eligibility, registered a phone, or connected Spotify in the last day, and every account sharing a device, address-day, or Spotify account with one of them, and raises rule flags for staff (migrations 0043, 0045, 0054; `services/api/src/features/fraud/README.md`). |
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

## Retention (`Retention` section, all unset)
Periods the owner has not decided yet (PROJECT_TODO "Decide retention for ..."; proposals in
`services/api/src/features/privacy/DATA_INVENTORY.md`). Unset keeps the record for good, as today; setting one turns
that pruning on in the daily `privacy.retention` job (`prune_optional_records()`, migration 0060). Deciding is then a
configuration change in the overlay, not code. Values are .NET time spans (`180.00:00:00` is 180 days).

| Setting | Prunes | Proposal |
| --- | --- | --- |
| `Retention__ReadNotifications` | notifications this long after they were read | 180 days |
| `Retention__Notifications` | notifications this long after they were written, read or not | 1 year |
| `Retention__ClickLinks` | which fan made an outbound ticket or merch click (the click stays, unlinked) | 1 year |
| `Retention__OcrText` | a submission's OCR text (`submissions.extracted`) this long after it was verified or rejected; the order-reference hash stays | 30 days |

## Liveness and integrations (`Health` section)
The worker serves no traffic, so Kubernetes cannot tell a stuck process from a quiet one. Each job loop reports to
`WorkerHeartbeat` every iteration; with `Health__Port` set (the container image sets `8081`), `GET /healthz` answers
`200 {"status":"ok", ...}` while every loop has reported within `Health__StaleAfter` (default 10 minutes, twice the
lease) and `503 {"status":"stale", ...}` otherwise, which fails the pod's liveness probe
(`infrastructure/k8s/base/worker-deployment.yaml`). A loop that cannot reach the database keeps reporting: that is
logged (`Job loop error`) and alerted on, since a restart would not help. Locally the port is unset and nothing listens.

`GET /healthz/integrations` on the same port probes the outside services the worker calls, with the credentials the
pod mounts: each `IIntegrationCheck` (`hosting/IntegrationCheck.cs`) makes one cheap real request and reports
`ok`, `off` (not configured), `refused` (the service rejected the key: 401 or 403), or `unavailable` (unreachable, rate
limited, or a server error; 25-second limit). The answer is `200` when every check is ok or off and `503` otherwise,
with one entry per integration: `{"status":"ok","integrations":{"ticketmaster":{"status":"ok","detail":null}}}`.
The deploy script calls it through a port-forward after every rollout and fails the deploy on `refused`
(`infrastructure/k8s/deploy.sh`, "Integrations"). Checks today: Ticketmaster (`concerts/TicketmasterCheck.cs`, one
classifications call). Requests are handled concurrently, so a slow probe never delays the liveness answer. By hand:
```bash
kubectl -n fandomscore port-forward deployment/worker 18081:8081 &
curl -s http://127.0.0.1:18081/healthz/integrations; kill %1
```

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
