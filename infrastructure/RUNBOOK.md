# On-call runbook

What to do when an alert arrives or a user reports a problem on **beta** or **prod**. Procedures with more detail
(deploys, rollbacks, restores, secret rotation, on-sale pre-scaling) are in [README.md](README.md); this page is the
short path from a symptom to an action. Every alert below emails `alert_email` (Terraform `monitoring.tf`).

## Where things are

| | beta | prod |
| --- | --- | --- |
| GCP project | `project-ef36a8a9-d958-4163-b4c` (display name firstline) | `fandomscore-prod` |
| GKE cluster (regional, `us-central1`) | `fandomscore-beta` | `fandomscore-prod` |
| Namespace | `fandomscore` | `fandomscore` |
| Cloud SQL instance | `fandomscore-beta` | `fandomscore-prod` |
| Dashboard | Terraform output `dashboard_url` | Terraform output `dashboard_url` |

```bash
ENV=beta   # or prod
gcloud config set project fandomscore-$ENV
gcloud container clusters get-credentials fandomscore-$ENV --region us-central1
kubectl config set-context --current --namespace fandomscore
```

Useful log queries (Logs Explorer):

```
# Everything from the API, worker, and redaction
resource.type="k8s_container" resource.labels.namespace_name="fandomscore"
# Only errors (the processes log JSON; LogLevel is the .NET level)
resource.type="k8s_container" resource.labels.namespace_name="fandomscore" (severity>=ERROR OR jsonPayload.LogLevel="Error")
# One request (the API logs the trace id from the load balancer)
resource.type="k8s_container" resource.labels.container_name="api" jsonPayload.Message:"<text>"
# Load balancer 5xx (502/503 mean no healthy backend)
resource.type="http_load_balancer" httpRequest.status>=500
```

## First five minutes

1. Open the dashboard. Is traffic arriving (requests per second)? Are the 5xx and latency lines flat? How many
   containers are running per workload?
2. `kubectl get pods` and `kubectl get events --sort-by=.lastTimestamp | tail -20`. Pods not `Running`/`Ready`:
   `kubectl describe pod <pod>` (events at the bottom), `kubectl logs <pod> --previous` if it restarted.
3. Was there a deploy? GitHub Actions → Deploy. The deployed commit is the image tag:
   `kubectl get deployment api -o jsonpath='{.spec.template.spec.containers[0].image}'`.
4. Is it the database? Cloud SQL → instance → Overview (CPU, connections, "instance is down" banners), then
   Query Insights.
5. If a recent deploy is the likely cause, roll back first and investigate after (below).

## Alerts

### API down
The uptime check on `https://<api_domain>/v1/health` fails from more than one region for five minutes.

- `kubectl get pods -l app.kubernetes.io/component=api`: if none are Ready, read `kubectl describe pod` (image pull,
  a secret without a version, probe failures) and the pod logs. A pod that starts and then fails readiness usually
  cannot reach the database: check `ConnectionStrings__Database` and `Database__ServerCa` have versions and that the
  Cloud SQL instance is up.
- Pods are Ready but the check fails: the load balancer. `kubectl describe gateway api` (status conditions),
  `kubectl describe httproute api`, Certificate Manager → certificate state (DNS authorization CNAME still in
  place?), and the Cloud Armor policy's recent denials (Network Security → Cloud Armor → policy → logs).
- Everything looks healthy from inside: DNS. `dig <api_domain>` must return `api_ip_address`.

### API returning errors
More than 5 % of load-balancer responses were 5xx over five minutes.

- 502/503 in the load balancer logs: backends failing health checks (`HealthCheckPolicy` on `/v1/health`). Treat as
  "API down" above.
- 500 from the API itself: the errors query above shows the exception. If it started with a deploy, roll back. If
  it is one endpoint, it may be data: note the request, fix forward.
- 429 is not counted here; a burst of 429 is Cloud Armor's per-IP throttle or the sign-in rate limit doing its job.

### Pods restarting
A container restarted more than twice in ten minutes.

- `kubectl logs <pod> --previous` shows why it died. Startup exceptions (configuration) end the process with a
  stack trace; the liveness probe shows as `Liveness probe failed` in `kubectl describe pod` events.
- `worker` or `redaction` restarting on liveness: a job loop stopped reporting for ten minutes, meaning a handler hung (network
  call without a timeout, a lock). The job is in the `jobs` table with `locked_by` set; its lease expires and it
  runs again, so a job that hangs every time restarts the pod every ten minutes. Find it
  (`SELECT id, kind, attempts, last_error FROM jobs WHERE status = 'running' ORDER BY locked_until`), mark it
  failed (`UPDATE jobs SET status = 'failed', finished_at = now(), locked_by = NULL, locked_until = NULL WHERE id = ...`),
  and fix the handler.
- `OOMKilled` in the describe output: the memory limit (512 Mi) is too small for the work, or there is a leak.
  Raise the limit in the manifest for now; look at the memory chart on the dashboard for the trend.

### Worker gave up on a job
A job reached status `failed` (every attempt failed, no handler for its kind, or its lease expired every time).

```sql
SELECT id, kind, attempts, last_error, payload, finished_at FROM jobs WHERE status = 'failed' ORDER BY finished_at DESC;
```

- A provider error (Spotify 5xx, token revoked): fix the cause if there is one, then retry the job
  (`workers/README.md`, Operating). Handlers are idempotent; retrying is safe.
- `No handler for job kind`: the API enqueued a kind the deployed worker does not know, which happens when the
  worker image is older than the API's. Check the Deployments run the same tag; redeploy.
- `submissions.redact` or `storage.cleanup` failing with a Cloud Storage 403: they run in the `redaction` Deployment,
  whose identity has the bucket access (`storage.tf`). In the `worker` they would fail; check `Jobs__OnlyKinds` and
  `Jobs__ExceptKinds` (infrastructure/README.md, Known limitations) and the `redaction` service account annotation.
- The same kind keeps failing after a deploy: a bug in the handler. Roll back the worker, or fix forward.

### Worker cannot reach the queue
More than twenty job loop errors in five minutes: the worker is up but cannot talk to the database.

- Also seeing "API down"? The database is the common cause: Cloud SQL → instance status, maintenance, or a failed
  storage resize. The API's `/v1/health` does not touch the database, so the API can look fine while nothing works.
- Only the worker (or only `redaction`): its secrets (`worker-secrets` / `redaction-secrets` in the overlay) or its identity. `kubectl logs deployment/worker`
  has the Npgsql exception; `kubectl describe pod` shows secret mount failures.
- Jobs queue up while the worker is out and drain afterwards (concurrency 2 per pod); nothing is lost.

### Cloud SQL under pressure
CPU above 80 % or memory above 90 % for fifteen minutes.

- Query Insights (Cloud SQL → instance → Query Insights) shows the top queries by load; a missing index shows as one
  query dominating. Add the index in a migration (`CREATE INDEX CONCURRENTLY`, its own migration).
- Connections: each API pod holds up to 20; the chart shows the total. More pods than the tier's limit allows means
  connection errors in the API logs. Scale the API down, or the database up.
- A bigger tier (`sql_tier` in `terraform/envs/<env>/main.tf`) restarts the instance (a minute or so, less with HA
  on prod). Do it in the maintenance window unless users are affected now.

### Budget
50 %, 90 %, 100 % of the monthly budget, or a forecast above 100 %. Billing → Reports, group by service. The usual
suspects are the cluster (pods requesting more than they use), Cloud SQL (tier, HA), and egress. Nothing is deleted
automatically.

## Common actions

- **Roll back a deploy.** `infrastructure/k8s/deploy.sh $ENV <previous-sha>` from a machine with the tools (images
  are still in the registry; the migrate Job finds nothing to do; `SKIP_BACKUP=1` is fine). Immediate stop-gap:
  `kubectl rollout undo deployment/api` (and `deployment/worker`, `deployment/redaction`). Migrations are never rolled back; the previous
  version runs against the current schema (README, "Changing beta (and prod) without breaking them").
- **Restart pods** (after adding a secret version): `kubectl rollout restart deployment/api deployment/worker deployment/redaction`.
- **More API capacity now:** `kubectl patch hpa api -p '{"spec":{"minReplicas":6}}'`; put it back afterwards.
  Before a scheduled on-sale, README "On-sale traffic".
- **Stop everything that writes** (before a restore): `kubectl scale deployment/api deployment/worker deployment/redaction --replicas=0`.
- **Restore the database:** README "Backups and restore". Prefer a point-in-time clone on prod.
- **A secret is wrong:** add a new version (`gcloud secrets versions add <name> --data-file=-`), restart the
  Deployment. The database connection string and server CA are written by Terraform; `terraform apply` rewrites
  them.
- **Block an abusive client:** add a Cloud Armor rule above priority 2000 in `edge.tf` and apply, or for a quick
  manual block, Network Security → Cloud Armor → policy → Add rule (Terraform will want to remove it later; copy it
  into `edge.tf`).

## Afterwards

Write down what happened and what was done (time, alert, cause, fix) in `docs/incidents/<date>-<slug>.md`, and turn
anything that would have shortened the outage into a TODO item (an alert, a dashboard line, a runbook entry).
