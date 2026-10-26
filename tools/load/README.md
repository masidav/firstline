# Load tests

## Allocation on-sale (`run.sh`)
What happens to the database when an allocation opens and every eligible fan registers and refreshes the show page
at once. Needs Docker and the .NET SDK; uses a throwaway PostgreSQL 16 container, never a real database.

```bash
tools/load/run.sh                              # 20,000 fans; registrations at 10, 50, and 200 clients
FANS=50000 CLIENTS="50 100" tools/load/run.sh  # bigger, other client counts
```

It applies the migrations, loads one show per client count (each with an open allocation of 2,000 tickets) and
`FANS` fans with a hub city and a score (`setup.sql`), then with pgbench:
1. **Registrations** (`register.sql`): every fan calls `register_for_allocation()` once, at each client count.
   Checks that every fan ends up registered exactly once and no transaction fails.
2. **Standing per request** (`standing_me.sql`): what each `GET /allocations/{id}/eligibility` asks the database,
   the asking fan's own place and score.
3. **Standing snapshot** (`snapshot_load.sql`): what `StandingSnapshots` loads, at most once per allocation every
   few seconds per API pod.

## Results (2026-10-26, 12-core dev machine shared with other containers)
Repeated runs vary by about a third with the machine's other load; the first run of a fresh database is slower.

| What | Clients | Throughput | Average latency |
| --- | --- | --- | --- |
| Register (20,000 fans, all registered once, 0 failures) | 10 | 1,800–4,700/s | 2–6 ms |
| Register | 50 | 4,100–5,100/s | 10–12 ms |
| Register | 200 | 4,000–4,200/s | 48–50 ms |
| Standing before (counted every registration in SQL) | 50 | ~420/s | ~120 ms |
| Standing now, per request | 50 | 15,500–16,800/s | ~3 ms |
| Standing snapshot load (20,000 registrations) | 1 | ~100/s | 9–11 ms |
| The draw, `run_allocation_selection()` (20,000 registrations, 2,000 tickets) | 1 | once | 7.7 s |

What this told us:
- **Registering scales to the cores and then flattens.** Between 50 and 200 clients throughput stays about the same and
  latency grows with the queue. Every registration takes `FOR SHARE` on the same allocation, show, and artist rows;
  without those locks (measured with a copy of the function) throughput at 200 clients was about 10% higher and the
  same at 50. Not worth loosening the locks that keep registration and the draw consistent. Keep API pods' database
  pools well under the database's cores times a small factor rather than letting hundreds of connections queue.
- **The show page was the bottleneck, not registering.** The standing used to count every registration on every
  request: about 10 ms at 20,000 registrants, growing with each one, so fans refreshing during an on-sale would have
  saturated the database long before registrations did. It now comes from an in-memory snapshot per allocation
  (`services/api/src/features/allocations/StandingSnapshots.cs`), refreshed at most every
  `Allocations:StandingSnapshotSeconds` (5), one query at a time per pod.
- **The draw is linear and synchronous.** About 0.4 ms per registration (one update and its triggers each), all inside
  the staff request. Fine at 20,000; somewhere past 75,000 registrants it would outlast a 30-second load-balancer
  timeout. Before an allocation that large, run the draw as a job (`enqueue_job()`) and let staff poll its status.
  Measured by hand on the load-test database; `run.sh` doesn't run it.

Not covered yet: the full HTTP path through the API (sign-in, JSON, the rate limiter, Cloud Armor). Next step is a
k6 script against a deployed beta (`docs/plans/ui-testing-and-launch.md`).
