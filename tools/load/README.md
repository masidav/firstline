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

The full HTTP path is in the next section.

## Full HTTP path (`http/run.sh`)
The same on-sale through the API itself, with k6 (`http/onsale.js`, run from the `grafana/k6` image; nothing to
install). Each k6 iteration is one signed-in fan doing what the show screen does
(`apps/mobile/src/features/ticketing/ShowScreen.tsx`): open the show (`GET /events/{id}`), load their eligibility and
standing (`GET /allocations/{id}/eligibility`), refresh it twice while deciding (up to 2 s apart), register
(`PUT /allocations/{id}/registration`; one fan in ten taps twice at once), and load the eligibility again, as the
screen does after registering. That is about 6 requests per fan. Fans arrive at a rate that ramps from 1 to a quarter
of `PEAK` in 30 s, holds 30 s, spikes to `PEAK` fans a second in 10 s (the on-sale notification), holds `HOLD` (60) s,
and stops.

```bash
tools/load/http/run.sh                                         # peaks of 25, 50, 100, 200 fans/s; API sized like one pod
LEVELS="200 400" API_CPUS=2 API_POOL=80 tools/load/http/run.sh # an API worth ~4 pods, to find the database's limit
```

`run.sh` starts a throwaway PostgreSQL 16 (2 CPUs, like prod's Cloud SQL tier) and the API's production image
(Dockerfile target `api`: Release, `Production` environment) limited to `API_CPUS` (0.5, the pod's CPU limit) and
512 MB, with a pool of `API_POOL` (20, as in the cloud connection string), all in Docker. For each level it loads a
fresh artist, show, allocation (2,000 tickets), and enough fans (`http/setup.sql`) with a hub city, a score, and a
session row, then runs k6 on the same Docker network. k6 signs each fan's access token itself (HS256 with `sub` and
`sid`, as `SessionService` does) with a signing key `run.sh` generates for the run and throws away; the API checks
the session in the database as for any fan. No real secret is involved. `BASE_URL` points the script anywhere else.

Thresholds (k6 exits non-zero if any fails): p95 under 500 ms overall and for each of event, eligibility, and register;
under 1 % failed requests; no 5xx or dropped connections; no 429; every check passing. Correctness: each fan's
registration must come back created exactly once (a double tap gets the same registration, 201 or 200) and show on
their eligibility afterwards, and after the run `run.sh` checks in the database that every fan who reached the
register step holds exactly one registration. It also samples the API's and database's CPU (`docker stats`) and saves
the database's top statements (`pg_stat_statements`) per level in the output directory.

### Results (2026-11-01, 12-core dev machine shared with other containers; k6 on the same machine)
Requests per second at the peak are about 6 times the fans per second. Docker CPU limits are CFS quotas on this
machine's cores, which are likely faster than an Autopilot vCPU, so read the pod counts below as a minimum.

| API | Peak fans/s (≈ requests/s) | Requests | p50 / p95 / p99 | Errors | Registered once | API CPU peak | DB CPU avg / peak |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0.5 CPU, pool 20 | 25 (≈150) | 12,594 | 1 / 3 / 18 ms | 0 | 2,064 of 2,064 | 0.39 | 0.07 / 0.17 |
| 0.5 CPU, pool 20 | 50 (≈300) | 25,403 | 1 / 3 / 29 ms | 0 | 4,164 of 4,164 | 0.50 (limit) | 0.13 / 0.27 |
| 0.5 CPU, pool 20 | 100 (≈600) | 50,393 | 2 / 39 / 68 ms | 0 | 8,264 of 8,264 | 0.53 (limit) | 0.26 / 0.62 |
| 0.5 CPU, pool 20 | 150 (≈900) | 75,725 | 5 / 82 / 115 ms | 0 | 12,414 of 12,414 | 0.51 (limit) | 0.35 / 0.76 |
| 0.5 CPU, pool 20 | 200 (≈1,200) | 86,107 | 1.3 s / 3.6 s / 3.8 s | 0 | 14,114 of 14,114 | 0.51 (limit) | 0.37 / 0.67 |
| 2 CPU, pool 80 | 200 (≈1,200) | 100,771 | 1 / 2 / 3 ms | 0 | 16,514 of 16,514 | 1.57 | 0.45 / 0.76 |
| 2 CPU, pool 80 | 300 (≈1,800) | 151,029 | 1 / 2 / 4 ms | 0 | 24,764 of 24,764 | 1.89 | 0.59 / 1.02 |
| 2 CPU, pool 80 | 400 (≈2,400) | 201,341 | 1 / 7 / 75 ms | 0 | 33,014 of 33,014 | 2.07 (limit) | 0.90 / 2.01 (limit) |

Every level passed its thresholds except 0.5 CPU at 200 fans/s (p95 3.6 s); even there no request failed and every fan
was registered exactly once. These are the numbers after the fixes below. Before them:

| Before the fixes | Peak fans/s | p95 | What happened |
| --- | --- | --- | --- |
| 0.5 CPU, pool 20 | 100 | 57 ms | Passed, but the database used 0.72 cores on average for ~350 requests/s |
| 0.5 CPU, pool 20 | 150 | 18 s | 6,046 requests failed (500 or none), 355 fans who tried weren't registered |
| 0.5 CPU, pool 20 | 200 | 24 s | 14,835 failed, half the fans who tried weren't registered; 33,752 errors logged |
| 2 CPU, pool 80 | 200 | 287 ms | Database at its 2 CPUs |
| 2 CPU, pool 80 | 300 and 400 | 3.3 s | Database at its 2 CPUs |

What this found and fixed (`services/api/Program.cs`):
- **The standing snapshot deadlocked the connection pool.** An eligibility request holds a connection while it waits
  for the allocation's standing snapshot, and the snapshot loaded from the same pool. Under load all 20 connections
  were held by requests waiting for a snapshot that was itself waiting for a connection, until the pool's 15 s timeout
  failed all of them with 500. That, not CPU, turned an overloaded pod into a failing one. The snapshot now has its own
  pool of 4 (`StandingSnapshots.DataSourceKey`); a test with a one-connection pool fails without it
  (`RegistrationApiTests`). An overloaded pod now only gets slower.
- **Planning, not running, was most of the database's CPU.** Npgsql sent every statement unprepared, so each was
  parsed and planned on every request, and its reset of a connection returned to the pool (`DISCARD ALL`) also threw
  away the plans PL/pgSQL caches, so `registration_blocker()` and `register_for_allocation()` were planned again every
  time (0.78 and 2.7 ms a call; 0.2 and 1.0 ms without the reset). The API now turns on automatic preparation
  (`Max Auto Prepare=100`) and turns off the reset. Together they took the database from 1.2 cores to 0.45 at 200
  fans/s and p95 from 287 ms to 2 ms.
- Not changed: **the API's own CPU**, about 0.5 ms per request (JWT, the session check, JSON, 5 to 6 queries for
  eligibility). If the database becomes the limit, eligibility could do its queries in one round trip.

What it means for the cloud (`infrastructure/k8s/base/api-hpa.yaml`, `infrastructure/README.md` "On-sale traffic"):
- **One pod (500m) holds about 150 fans a second at the peak** (≈900 requests/s, p95 under 100 ms), and past that it
  slows down rather than failing. Plan on half: **75 fans a second per pod.** Pre-scale `minReplicas` to
  ceil(expected peak fans per second x 3 / 75), the 3 being the launch plan's margin on the expected peak. Example:
  20,000 fans in the first two minutes is ~170 fans/s; times 3 is 500, so 7 pods.
- **The HPA can't help within a spike.** It targets 70 % of the 250m request (175m, about a third of what a pod can
  do), which leaves headroom between on-sales, but scaling up takes a metrics window plus Autopilot provisioning
  (minutes), and the spike here goes from a quarter to full in 10 s. Keep the HPA as it is; pre-scaling is what
  covers an on-sale. Prod's `maxReplicas` of 20 is ~1,500 fans/s of pods, more than the database below.
- **The database is the ceiling: about 400 fans a second on 2 CPUs** (≈2,400 requests/s, where it reached its 2
  CPUs). Prod's `db-custom-2-7680` has 2 vCPUs; for more than ~300 fans/s expected (with the margin), raise the tier
  the day before.
- **Connections:** each API pod now opens up to 24 (20 + 4 for the snapshot). 10 pods is 240 plus the worker's;
  check `SHOW max_connections` on Cloud SQL (its default depends on the tier's memory) before pre-scaling that far.
- **Beta can't measure prod's capacity:** it runs at most 6 pods and a shared-core `db-g1-small`. A beta run checks
  the deployed path (Gateway, TLS, Cloud Armor, Cloud SQL over the network, real pods), not the ceiling.

### Running it on beta
Not run yet. Beta has real testers, so only outside their hours, announced, with a test artist nobody follows, and
with someone watching the dashboard (Terraform output `dashboard_url`). Needs `kubectl` on the beta cluster
(`gcloud container clusters get-credentials fandomscore-beta ...`, as for `infrastructure/k8s/db-shell.sh`).

1. **Pick a run tag and a city.** A new tag for each pass (`beta1`, `beta2`: it names the rows and their ids). The
   show's city and the other hub must be no real tester's hub, or the test show turns up in their feed; this should
   return no rows:
   `echo "SELECT city_id, count(*) FROM user_hub_cities WHERE city_id IN ('us-ak-anchorage', 'us-hi-honolulu') GROUP BY 1;" | infrastructure/k8s/db-shell.sh beta`
2. **Pre-scale** as for a real on-sale (`infrastructure/README.md` "On-sale traffic"): beta's ceiling is 6 pods, so
   `kubectl -n fandomscore patch hpa api -p '{"spec":{"minReplicas":6}}'` 15 minutes ahead, and check that
   `kubectl -n fandomscore get pods -l app.kubernetes.io/component=api` shows 6 ready. Check beta's
   `SHOW max_connections` first: 6 pods can open 144.
3. **Test accounts and an isolated allocation** (`http/setup.sql` through the cluster's database shell): synthetic fans
   `lt<tag>_<n>@example.test` with sessions, an artist "Load Test <tag> (ignore)", and one show with one allocation
   that opens 3 s after the script runs, so run step 4 right after.
   ```bash
   { printf '\\set run beta1\n\\set fans 5000\n\\set tickets 2000\n\\set city us-ak-anchorage\n\\set other_city us-hi-honolulu\n'
     cat tools/load/http/setup.sql; } | infrastructure/k8s/db-shell.sh beta
   ```
   `fans` must cover the pass: about 83 x `PEAK` + 5 % with the default stages (5,000 is enough for `PEAK=50`).
4. **Capacity pass, inside the cluster** (`http/k6-job.yaml`): a Job running k6 as the api identity, with
   `Auth__SigningKey` mounted from Secret Manager the way the API pods get it, so the key never leaves the cluster.
   It calls the api Service directly, without the load balancer and Cloud Armor. Set `RUN`, `FANS`, and `PEAK` in the
   file (start at `PEAK=25`, then 50), then:
   ```bash
   kubectl -n fandomscore create configmap load-onsale --from-file=tools/load/http/onsale.js
   kubectl -n fandomscore apply -f tools/load/http/k6-job.yaml
   kubectl -n fandomscore logs -f job/load-onsale        # k6's summary; the Job fails if a threshold did
   ```
   Every fan registers once, so each pass needs a fresh tag: step 3 again with `beta2`, delete the Job, and apply it
   with the new values. Afterwards, check every fan who tried is registered once:
   `echo "SELECT count(*), count(DISTINCT user_id) FROM allocation_registrations WHERE allocation_id = md5('fandom-load:beta1:allocation')::uuid;" | infrastructure/k8s/db-shell.sh beta`
   (both equal to k6's `register_attempts`).
5. **Edge pass, through Cloud Armor.** The same Job with `BASE_URL=https://api.beta.firstline-5730kl.com` and a fresh
   tag. All its requests come from the cluster's one egress address, and Cloud Armor throttles each client address
   at 600 requests a minute (`edge_rate_limit_per_minute`, `infrastructure/terraform/modules/environment/edge.tf`), so
   keep it under 10 requests/s: `PEAK=1` (about 6/s) should pass with no 429. At `PEAK=2` and above, expect the
   `rate_limited` threshold to fail: that is the throttle working (Cloud Armor answers with its own 429, not
   problem+json). Don't change the rule for a test. Worth knowing for real on-sales: fans behind one address (a
   campus, a carrier NAT) share those 600 a minute, about 100 fans a minute at this script's 6 requests each.
6. **Clean up** each tag, and put the HPA back:
   ```bash
   for tag in beta1 beta2; do { printf '\\set run %s\n' "$tag"; cat tools/load/http/cleanup.sql; } | infrastructure/k8s/db-shell.sh beta; done
   kubectl -n fandomscore delete job/load-onsale configmap/load-onsale
   kubectl -n fandomscore patch hpa api -p '{"spec":{"minReplicas":2}}'   # the beta overlay's value
   ```
   `cleanup.sql` cancels the allocation (which withdraws every registration), pauses the artist, and deletes the
   synthetic fans the way account deletion does (handle and email scrubbed, sessions ended, so their tokens stop
   working). Registrations and the audit are never deleted, by design. It ends by printing `live_registrations 0,
   fans_left 0`.

Record the results here in the same format, with beta's tier and pod count.
