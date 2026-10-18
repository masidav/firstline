# ADR 0004: Background jobs on a small Postgres queue

- **Status:** Accepted (2026-10-18). Resolves the job-library question left open in ADR 0001.
- **Code:** `workers/` (`FandomScore.Worker`), `database/migrations/0004_jobs_and_retention.sql`.

## Decision
Background work runs from a `jobs` table in the main PostgreSQL database, processed by the `worker` process.
No Hangfire, Quartz.NET, Wolverine, or cloud queue.

| Concern | How |
| --- | --- |
| Enqueue | `SELECT enqueue_job(kind, payload, dedupe_key, run_at, max_attempts)`, from the API, SQL functions, or jobs, **in the same transaction** as the change that needs the work. If the change rolls back, so does the job. |
| Duplicates | Optional `dedupe_key`: at most one *queued* job per key. A new job can be queued while one with the same key is running, so changes made during a run are not lost. |
| Claiming | `UPDATE ... FROM (SELECT ... FOR UPDATE SKIP LOCKED)`: any number of worker replicas share the queue, and no job goes to two workers. |
| Crashes | A claimed job has a lease (`locked_until`, 5 minutes by default). If the worker dies, the job is claimed again after the lease. **Handlers must be idempotent.** |
| Retries | Failures retry with exponential backoff (30 s doubling, capped at 1 hour, ±20% jitter) up to `max_attempts` (default 10), then the job is `failed`. Unknown job kinds fail at once. |
| Errors | `last_error` keeps the exception type and message. Handlers must not put secrets or personal data in exception messages. |
| Recurring jobs | `job_schedules` (kind, interval). Handlers declare `Every`; the worker registers them at startup and `fire_due_schedules()` enqueues each due one exactly once. |
| Shutdown | A job interrupted by shutdown goes back to the queue without counting the attempt. |
| Cleanup | `jobs.prune` (daily) deletes succeeded jobs after 7 days and failed ones after 30. |

Handlers implement `IJobHandler` (`Kind`, optional `Every`, `RunAsync`) and live in feature folders under
`workers/` (e.g. `workers/account-retention/`); register each in `workers/Program.cs`.

## Why
- **Transactional enqueue** is the main requirement: adding an artist and queuing its onboarding must succeed
  or fail together. A queue in the same database gives that for free; external queues need an outbox.
- **No new infrastructure:** no Redis, no extra managed service, nothing extra to run locally or in tests.
- **Small and owned:** about 300 lines, tested against a real database, with no framework to learn.
- **Fits the load:** per-user and per-artist jobs, well within what `SKIP LOCKED` queues handle.

## Consequences
- No dashboard. Query `jobs` directly (`status`, `last_error`, `attempts`); add monitoring in Phase 12
  (queued count, oldest queued age, failed count).
- Polling (every 2 s when idle) adds light database load. `LISTEN/NOTIFY` can cut latency later if needed.
- If on-sale registration spikes (Phase 11) outgrow it, move those specific jobs to Pub/Sub behind the same
  `enqueue_job` call, using an outbox.
