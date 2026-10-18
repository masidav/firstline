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
| `account-retention/` | `PurgeDeletedAccountsJob`: hourly, purges content of accounts deleted 30+ days ago. |
| `spotify-sync/` | `SpotifySyncJob` (`spotify.sync`) and hourly `ScheduleSpotifySyncsJob`. See [ADR 0005](../docs/adr/0005-spotify-integration.md). |
| `artist-onboarding/`, `scoring/`, ... | Planned job handlers (see each `TODO.md`). |

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

## Operating
- Inspect: `SELECT kind, status, attempts, last_error, run_at FROM jobs WHERE status <> 'succeeded' ORDER BY run_at;`
- Retry a failed job: `UPDATE jobs SET status = 'queued', finished_at = NULL, attempts = 0, run_at = now() WHERE id = ...;`
- Any number of worker replicas can run; claims never overlap.

## Tests
`tests/FandomScore.Tests/JobQueueTests.cs`: success, dedupe, scheduled runs, retries with backoff, permanent
failure, unknown kinds, crashed-worker recovery, concurrent claims, and the deleted-account purge.
