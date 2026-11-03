# Operating beta and prod: backups, recovery, monitoring

The reference for keeping the cloud environments alive. `RUNBOOK.md` is the short path from an alert to an action;
this page has the full procedures it points to. `README.md` covers setup and how deploys work.

Values here come from Terraform (`terraform/modules/environment`, `terraform/envs/{beta,prod}`) and were checked
against the live beta project on 2026-11-03 (read-only `gcloud ... describe/list`, `kubectl get`). Prod does not
exist yet (`docs/OWNER_TODO.md`, section E): its numbers are what Terraform will create.

Every procedure is marked **rehearsed** (done for real on beta, with the date) or **not rehearsed** (written from
the Google documentation and the `--help` of the installed gcloud 590; expect surprises the first time).

## Setting up a shell

```bash
ENV=beta
PROJECT=project-ef36a8a9-d958-4163-b4c        # prod: fandomscore-prod (or the project_id chosen for it)
INSTANCE=fandomscore-$ENV                     # Cloud SQL instance; the cluster has the same name
gcloud config set project $PROJECT
gcloud container clusters get-credentials fandomscore-$ENV --region us-central1 --project $PROJECT
kubectl config set-context --current --namespace fandomscore
```

Terraform: `cd infrastructure/terraform/envs/$ENV` (state in `gs://$PROJECT-tfstate`, `terraform.tfvars` is local
and gitignored). `kubectl` and `terraform` on the owner's machine live in `~/.local/bin`.

## Deployment, in one paragraph

A push to `main` runs CI and, independently, the Deploy workflow (`.github/workflows/deploy.yml`): it builds the
images once, tagged with the commit SHA, and `infrastructure/k8s/deploy.sh` takes a Cloud SQL backup, runs the
migrations as a Kubernetes Job, rolls the Deployments, and polls the public health endpoint. Prod gets the same image
digests after a manual approval. The details, the rollback commands, and turning an environment off and on are in
`README.md` ("Changing beta (and prod) without breaking them", "Deploying") and `RUNBOOK.md` ("Common actions");
those sections are the source of truth for deploys and are not repeated here. Two properties the recovery
procedures below rely on:

- **Migrations only move forward and stay compatible with the previous release** (expand, release, contract;
  `tools/ci/check_migration_safety.py` blocks drops, renames, and tightened constraints unless a migration says
  `-- allow-breaking:`). So the previous application version always runs correctly on the current schema, and a code
  rollback never needs a schema rollback.
- **Tagged images are never deleted** (Artifact Registry cleanup deletes only untagged images older than 30 days,
  `registry.tf`), so any earlier commit can be redeployed.

## Backups

### What is backed up

| What | How | Retention | Where |
| --- | --- | --- | --- |
| Database, daily | Cloud SQL automated backup, window starting 02:00 UTC (`sql.tf`) | Last **14** automated backups on beta, **30** on prod (`sql_backup_retention_days`, retention unit COUNT: one per day, so 14 / 30 days) | Multi-region `us` (default backup location) |
| Database, any point | Point-in-time recovery: transaction logs (`transaction_log_retention_days = 7`) | **7 days** on both. On beta 2026-11-03: earliest recovery time 2026-10-27T03:56Z, latest trailing the clock by about a minute (`gcloud sql instances get-latest-recovery-time $INSTANCE`) | Cloud Storage, managed by Cloud SQL |
| Database, before every deploy | `deploy.sh` runs `gcloud sql backups create --description "pre-deploy <sha>"` before the migrate Job (skipped with `SKIP_BACKUP=1`) | **Kept until someone deletes them**: on-demand backups don't count toward the 14/30 and are never pruned (37 of them on beta after 8 days) | Multi-region `us` |
| Archive bucket (`<project>-archive`: redacted proof) | Object versioning. A replaced or deleted file becomes a noncurrent version; the lifecycle rule deletes noncurrent versions **30 days** after they became noncurrent (`storage.tf`, commit 32ce75f) | 30 days, then 7 more days of soft delete. **Beta: the 30-day rule is not applied yet** (`docs/OWNER_TODO.md` item 9), so old versions are kept forever there until `terraform apply` | `us-central1` |
| All buckets (archive, quarantine, media, tfstate) | Cloud Storage soft delete, the bucket default (not set in Terraform) | **7 days** after deletion (`retentionDurationSeconds: 604800` on all four beta buckets) | Same bucket |
| Terraform state (`<project>-tfstate`) | Versioning (bootstrap.sh) plus soft delete | Old versions kept indefinitely | `us-central1` |
| Secrets | Secret Manager keeps every version until it is destroyed | Indefinite | Automatic replication |
| Code, manifests, Terraform, migrations | git (GitHub) | Indefinite | GitHub |
| Images | Artifact Registry, tagged with the commit SHA | Tagged: indefinite; untagged: 30 days; the newest 20 versions of each image always kept | `us-central1` |

There is no "uploads" bucket: uploads land in **quarantine** (unredacted, deleted after 30 days by the bucket's
lifecycle rule, `quarantine_retention_days`; verified proof is moved to the archive by redaction before then), and
**media** holds staff-published videos and profile pictures. Neither is versioned.

### What is not backed up

- **The quarantine and media buckets beyond soft delete.** A file deleted there (by the app, the lifecycle rule, or
  a person) can be restored for 7 days, then it is gone. Quarantine is short-lived by design (never archive
  unredacted files); media can be re-uploaded by staff and fans.
- **Anything outside `us-central1` except database backups.** Buckets, the cluster, and the database are in one
  region; there is no cross-region copy of files. Database backups are multi-region, so the data survives a region
  loss, but nothing is set up to run elsewhere.
- **The database after the instance is deleted.** `retainBackupsOnDelete` is `false` and no final backup is
  configured, so deleting the Cloud SQL instance deletes its automated *and* on-demand backups with it. On beta,
  `deletion_protection = false` in Terraform and the instance's own deletion protection
  (`deletionProtectionEnabled`) is off, so one `gcloud sql instances delete` or `terraform destroy` loses all beta
  data. On prod, Terraform's `deletion_protection = true` stops `terraform destroy`, but the instance-level flag is not
  set either (see "Gaps").
- **Secrets whose secret is deleted.** Deleting a secret (not a version) deletes all its versions. Every secret
  can be regenerated except one: losing `Spotify__TokenKey` makes every stored Spotify refresh token unreadable, and
  every fan must reconnect Spotify. There is no copy outside Secret Manager.
- **Logs beyond their retention**: `_Default` bucket 30 days (application, load balancer, Cloud Armor, Cloud SQL
  logs), `_Required` 400 days (admin audit logs). No sinks export them.
- **GitHub settings** (environments, variables `DEPLOY_ENABLED`, `PROD_DEPLOY_ENABLED`, `GCP_*`, reviewers):
  documented in README "Set up a cloud environment", step 6; recreated by hand.
- **Monitoring, alerts, dashboards, DNS records, certificates**: not backed up, but all come from Terraform and are
  recreated by `terraform apply`.

### RPO and RTO as configured

RPO is how much recent data a recovery loses; RTO is how long until the service is back. These are what the current
configuration allows, not targets anyone has committed to. Times marked *estimated* have never been measured.

| Failure | RPO | RTO |
| --- | --- | --- |
| Bad deploy (code) | 0 | ~2 min (`kubectl rollout undo`) to ~5 min (redeploy the previous SHA); detection: the deploy's own health check, or an alert after 5 minutes |
| Bad data (a bug, a bad migration, an operator mistake) noticed within 7 days | Minutes before the bad event (PITR to just before it) | *Estimated* 30 to 60 min for today's database size (clone, back up, restore into the original, redeploy); writes during the outage window are lost |
| Bad data noticed after 7 days | Up to 24 h (newest daily backup before the event) and only within 14 days (beta) / 30 days (prod); or the matching `pre-deploy` backup | Same as above |
| Cloud SQL zone outage | Beta (ZONAL): 0 if you wait for the zone, otherwise minutes (PITR clone into another zone). Prod (REGIONAL, HA): 0 | Beta: until Google restores the zone, or *estimated* 30 to 60 min via a PITR clone with `--preferred-zone`. Prod: automatic failover, about a minute |
| Cloud SQL instance deleted | **Everything** (backups go with it; see "What is not backed up") | Rebuild from nothing |
| GKE cluster lost | 0 (no state lives in the cluster) | *Estimated* 45 to 60 min: ~20 min for Terraform to recreate the Autopilot cluster, then a deploy (~10 min) and ~15 min for a new load balancer to answer |
| Region outage (`us-central1`) | Database: backups are multi-region, so at most a day from the newest daily backup (minutes if PITR is still readable); files: unavailable until the region returns (no copy elsewhere) | Not planned: Terraform, buckets, and the cluster are single-region. Hours to days |
| A file deleted from the archive | 0 (the noncurrent version) | Minutes, within 30 days (+7 soft delete) |
| A file deleted from quarantine or media | 0 within 7 days, then lost | Minutes |
| Leaked secret | n/a | ~5 min per secret (new version, restart pods); the database password ~10 min |

## Recovery procedures

### 1. A bad deploy: roll the application back — not rehearsed

Follow `README.md` "Deploying" ("Roll back") and `RUNBOOK.md` "Common actions" ("Roll back a deploy"); they own the
exact commands as deploys change. The shape: the fastest stop-gap is `kubectl rollout undo deployment/<name>` (one
revision back; the cluster keeps the earlier ReplicaSets), then a proper redeploy of the previous SHA so git, the
cluster, and the next deploy agree. Migrations stay as they are (see "Deployment, in one paragraph"); never roll a
schema back by hand.

Status: the deploy path itself runs on every push, but neither `rollout undo` nor a redeploy of an older SHA has
been done on beta: **not rehearsed**.

### 2. A bad migration — not rehearsed

How it can go wrong, and what to do in each case. DbUp runs each script in its own transaction
(`database/Migrator.cs`, `WithTransactionPerScript`) and records applied scripts in the `schemaversions` table.

1. **The migrate Job failed.** The failing script rolled back; scripts before it in the same deploy stayed applied.
   `deploy.sh` stops before the rollout, so the old pods keep serving, and they keep working on the partly migrated
   schema because every migration is additive. Read the Job's log (`kubectl logs job/migrate`, or the Logs Explorer
   query for `container_name="migrate"` below). Every later deploy retries the same script and fails the same way, so
   deploys are blocked until it is resolved.
   - If the script failed on data it didn't expect (a row violating a new constraint), the owner fixes that data
     with a one-off statement (`infrastructure/k8s/db-shell.sh $ENV`) and redeploys: the unchanged script then
     succeeds.
   - If the script itself is wrong: it is on `main`, and the rules forbid editing a migration on `main` (CI and a
     hook enforce it), even though it never applied anywhere. That is the owner's decision; stop and ask.
2. **The migration succeeded but the new code is broken.** Roll the code back (procedure 1). The new schema stays;
   the previous release runs on it by construction. If something the migration added must go, remove it in a later
   migration once no deployed version uses it.
3. **The migration succeeded but damaged data** (a wrong `UPDATE`, a backfill that overwrote values).
   - If the damage can be computed back from what's left, fix forward with a new migration. Points are corrected
     with `reverse_points()`; `score_events` is never updated or deleted.
   - Otherwise restore the database to just before the migrate Job: procedure 3 with the target time just before the
     `pre-deploy <sha>` backup's end time (`gcloud sql backups list --instance $INSTANCE --filter="description:'pre-deploy <sha>'"`,
     then `gcloud sql backups describe <id> --instance $INSTANCE` for `endTime`), and redeploy the commit *before*
     the bad one. Restoring the `pre-deploy` backup in place (`gcloud sql backups restore <id> --restore-instance
     $INSTANCE`) is the quicker variant; it skips the verification step.

### 3. Restore the database to a point in time — not rehearsed

**Never rehearsed.** The rehearsal on beta is an owner step (`docs/OWNER_TODO.md` section E, "Rehearse a Cloud SQL
point-in-time restore on beta and record the result"); record the measured times here and in
`infrastructure/TODO.md` when it's done.

The approach: clone the instance at the chosen time (the original keeps running untouched), verify the clone, then
copy its data back into the original instance with a backup and restore. Copying back keeps the instance name,
private IP, server CA, and Terraform state as they are, so `ConnectionStrings__Database`, `Database__ServerCa`,
`deploy.sh`'s backup step, and the alerts keep working. Pointing the app at the clone instead (variant B below) is
for when the original instance is unusable.

1. **Pick the time.** Find the bad event in the logs or the audit log (queries below) and choose a UTC time just
   before it. Check it is inside the window:
   ```bash
   gcloud sql instances get-latest-recovery-time $INSTANCE --project $PROJECT
   ```
2. **Clone.** Instance names can't be reused for a week after deletion, so date the clone's name. The clone inherits
   the source's tier, private network, and flags.
   ```bash
   CLONE=$INSTANCE-r$(date -u +%Y%m%d%H%M)
   gcloud sql instances clone $INSTANCE $CLONE --point-in-time 2026-11-03T08:30:00Z --project $PROJECT
   ```
3. **Verify the clone** (read-only). A copy of `db-shell.sh` that connects to the clone's private address (same
   user and password; the clone has its own server CA, so this uses `sslmode=require` instead of verifying it):
   ```bash
   CLONE_IP=$(gcloud sql instances describe $CLONE --project $PROJECT --format='value(ipAddresses[0].ipAddress)')
   sed -e "s/PGHOST=\$(get Host)/PGHOST=$CLONE_IP/" \
       -e "s|psql 'sslmode=verify-ca sslrootcert=/secrets/Database__ServerCa'|psql 'sslmode=require'|" \
       infrastructure/k8s/db-shell.sh > /tmp/clone-shell.sh
   bash /tmp/clone-shell.sh $ENV
   # e.g. SELECT max(created_at) FROM score_events; SELECT count(*) FROM users; the rows the incident damaged
   ```
4. **Stop deploys and writers.** A push during the restore would migrate the database under you.
   ```bash
   gh variable set DEPLOY_ENABLED --body false
   kubectl scale deployment/api deployment/worker deployment/redaction --replicas=0
   ```
   The API answers 502/503 from now on (the web apps stay up and show their error state). Jobs wait in the queue.
5. **Keep a way back**: a backup of the original as it is now, so the restore itself can be undone.
   ```bash
   gcloud sql backups create --instance $INSTANCE --project $PROJECT --description "before restore to <time>"
   ```
6. **Copy the clone's data into the original.** Back up the clone, then restore that backup into the original
   (this replaces all of the original's data and restarts it).
   ```bash
   gcloud sql backups create --instance $CLONE --project $PROJECT --description "restore point <time>"
   BACKUP_ID=$(gcloud sql backups list --instance $CLONE --project $PROJECT --limit 1 --format='value(id)')
   gcloud sql backups restore $BACKUP_ID --restore-instance $INSTANCE --backup-instance $CLONE --project $PROJECT
   ```
   The database users come from the backup; if the app password was changed after the restore point, run
   `terraform apply` once to set it again.
7. **Redeploy and reopen.** Redeploy the commit that should run (the current one, or the one before a bad
   migration; README "Deploying" has the command). The deploy re-runs any migrations newer than the restore point
   and puts the replica counts back. Then `gh variable set DEPLOY_ENABLED --body true`.
8. **Reconcile files.** The database is now older than the buckets. Files written after the restore point have no
   row (harmless; `storage.cleanup` won't know about them). Files the app deleted after the restore point but that
   rows now reference again: restore them with procedure 4 (archive within 30 days, quarantine and media within 7).
9. **Clean up** after a few days: `gcloud sql instances delete $CLONE --project $PROJECT`. It is not in Terraform
   and costs money while it exists.

**Variant B: run on the clone** (the original is gone or broken). Point the secrets at the clone and restart:
```bash
gcloud secrets versions access latest --secret ConnectionStrings__Database --project $PROJECT \
  | sed "s/Host=[^;]*/Host=$CLONE_IP/" \
  | gcloud secrets versions add ConnectionStrings__Database --project $PROJECT --data-file=-
gcloud sql instances describe $CLONE --project $PROJECT --format='value(serverCaCert.cert)' \
  | gcloud secrets versions add Database__ServerCa --project $PROJECT --data-file=-
kubectl rollout restart deployment/api deployment/worker deployment/redaction
```
(The password passes through your shell's pipe, never the screen or a file.) Then **do not run `terraform apply`**:
Terraform still describes the old instance and would write its address back into both secrets, or create a new empty
`fandomscore-<env>` instance. Deploys must use `SKIP_BACKUP=1` (`deploy.sh` backs up the instance named
`fandomscore-<env>`), and nothing backs the clone up on a schedule beyond its own automated backups. Moving Terraform
onto the clone needs a code change (the instance name is fixed in `sql.tf`); see "Gaps".

### 4. Restore a deleted or overwritten file — not rehearsed

A storage key `archive/<name>` is the object `<name>` in bucket `<project>-archive` (likewise `quarantine/`,
`media/`; `packages/storage/GcsFileStore.cs`). Restoring a file only helps if a database row still points at it
(`uploads.storage_key`, `submission_files.storage_key`); the app deletes files *because* their row was deleted.

**Archive (versioned):** list the versions, then copy the one you want back on top.
```bash
gcloud storage ls --all-versions --long gs://$PROJECT-archive/<name>
gcloud storage cp gs://$PROJECT-archive/<name>#<generation> gs://$PROJECT-archive/<name>
```
A version the 30-day rule already deleted is soft-deleted for 7 more days: use the soft-delete commands below.

**Quarantine, media (not versioned), or anything within 7 days of deletion:** soft delete.
```bash
gcloud storage ls --soft-deleted --long gs://$PROJECT-media/<name>
gcloud storage restore gs://$PROJECT-media/<name>#<generation>
```
Never copy a quarantine (unredacted) file into the archive by hand.

### 5. The cluster is lost — not rehearsed

Nothing durable lives in the cluster: data is in Cloud SQL and Cloud Storage, secrets in Secret Manager, the static
address, certificates, Cloud Armor policy, and DNS in Terraform outside the cluster, and the manifests in git. The
Workload Identity pool belongs to the project, so the service account bindings survive.

1. From `infrastructure/terraform/envs/$ENV`: `terraform plan` (it should want to create the cluster and only
   what depends on it; stop and look if it wants to replace the database, buckets, or secrets), then
   `terraform apply`. About 20 minutes for the Autopilot cluster (the first apply took that, 2026-10-26).
2. `gcloud container clusters get-credentials fandomscore-$ENV --region us-central1 --project $PROJECT`.
3. Deploy the current commit (Deploy workflow by hand: `gh workflow run deploy.yml -f environment=beta`, or
   `deploy.sh` per README "Deploying"). The deploy recreates the namespace, service accounts, secrets mounts, and the
   Gateway on the same static address; HTTPS answers about 5 minutes after the Gateway exists, the web apps about 15.
4. Check: `curl https://<api_domain>/v1/health`, the dashboard, and that the worker and redaction pods run.

Steps 2 to 4 are what every deploy and the beta "on" switch do; recreating the cluster itself has **not been
rehearsed**.

### 6. A secret leaked — not rehearsed

The pods read their secrets once, at start (the GKE Secret Manager add-on mounts `versions/latest`; rotation of
mounted files is not enabled), so every rotation is: add a version, restart what reads it, disable the old version.

```bash
openssl rand -base64 32 | tr -d '\n' | gcloud secrets versions add Auth__SigningKey --project $PROJECT --data-file=-
kubectl rollout restart deployment/api
gcloud secrets versions list Auth__SigningKey --project $PROJECT
gcloud secrets versions disable <old-version> --secret Auth__SigningKey --project $PROJECT
```

Who reads what (`k8s/overlays/<env>/secrets.yaml`) and what each rotation does:

| Secret | Readers | Rotate | Effect |
| --- | --- | --- | --- |
| `Auth__SigningKey` | api | New random value (above) | Access tokens (15 min) stop validating; apps refresh silently (refresh tokens live in the database, not signed with this key). Signed file links stop working. If sessions themselves may be compromised, also end them all: `UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'sign_out' WHERE revoked_at IS NULL;` (everyone signs in again) |
| `ConnectionStrings__Database` (password) | api, worker, redaction, migrate | `terraform apply -replace=module.environment.random_password.db` (sets the new password on the user and writes a new secret version), then restart api, worker, redaction | New connections fail until the pods restart: do the restart right after the apply |
| `Spotify__ClientSecret` | api, worker, deploy | Regenerate in the Spotify developer dashboard, add the version, restart api and worker | Artist search and the app token stop working until the restart |
| `Spotify__TokenKey` | api, worker | Only with the previous key kept for decryption: README "Deploying", "Secret rotation" (`Spotify__PreviousTokenKeys`) | Rotating without that makes every stored Spotify token unreadable (fans reconnect) |
| `Auth__Apple__PrivateKey` | api | Revoke the key in the Apple developer account, create a new one, add its `.p8` as a version, set the new key id (`Auth__Apple__KeyId`) in the overlay, deploy | Sign in with Apple fails between revocation and the deploy |
| `Ticketmaster__ApiKey` | worker | Regenerate at developer.ticketmaster.com, add the version, restart worker | Concert sync jobs fail and retry until the restart |
| `Database__ServerCa` | all database clients | Not a secret (a public certificate). After `gcloud sql ssl server-ca-certs rotate`, `terraform apply` writes the new one; restart the pods | |

Restart everything that reads a secret: `kubectl rollout restart deployment/api deployment/worker deployment/redaction`.
Who read a secret can't be found out afterwards: Data Access audit logs are not enabled for Secret Manager (no
`auditConfigs` on the project), so rotate a possibly leaked secret rather than investigate.

Status: secrets got their first versions at setup (2026-10-26) and pods pick them up on every deploy, but replacing a
version that is in use, disabling the old one, and the database password replacement have **not been rehearsed**.

## Monitoring

Everything below is defined in Terraform (`monitoring.tf`, `dashboard.tf`) and exists on beta (checked 2026-11-03:
7 alert policies enabled, 3 log-based metrics, 1 uptime check, dashboard "Fandom Score (beta)"). Every alert emails
the notification channel "Fandom Score <env> alerts" (`alert_email` in `terraform.tfvars`). The full symptom-to-action
steps are in `RUNBOOK.md` "Alerts"; this is the catalogue.

### Alert policies

| Alert (display name) | Fires when | First thing to check |
| --- | --- | --- |
| **API down** | The uptime check on `https://<api_domain>/v1/health` fails from more than one region for 5 minutes. `/v1/health` does not touch the database, so this means pods, load balancer, certificate, or DNS | `kubectl get pods -l app.kubernetes.io/component=api`; if Ready, the Gateway (`kubectl describe gateway api`) and the certificate |
| **API returning errors** | More than 5 % of load balancer responses are 5xx over 5 minutes (all hostnames on the load balancer, API and web apps together) | Load balancer log for 502/503 (no healthy backend) vs. 500 (the API threw: error query below); was there a deploy? |
| **Pods restarting** | A container in `fandomscore` restarted more than twice in 10 minutes | `kubectl describe pod <pod>` (OOMKilled? liveness?), `kubectl logs <pod> --previous` |
| **Worker gave up on a job** | Log-based metric `worker_jobs_given_up` > 0 in 10 minutes (a job reached status `failed`), per job kind | `SELECT id, kind, attempts, last_error FROM jobs WHERE status = 'failed' ORDER BY finished_at DESC;` |
| **Worker cannot reach the queue** | Log-based metric `worker_loop_errors` > 20 in 5 minutes | Cloud SQL instance status; the worker's log for the Npgsql exception |
| **Cloud SQL under pressure** | CPU > 80 % or memory > 90 %, averaged over 5 minutes, for 15 minutes | Query Insights (top queries), connection count on the dashboard |
| **Proof photos about to be lost** | The hourly `redaction.sweep` logs `Redaction overdue` (a verified file still in quarantine 25 days after upload; quarantine deletes at 30). Emails at most every 6 hours | Staff Photo redaction screen (`GET /v1/admin/redactions`), failed `submissions.redact` jobs, `kubectl logs deployment/redaction` |
| Budget (not an alert policy) | 50 %, 90 %, 100 % of `budget_monthly_usd`, or forecast above 100 % (`google_billing_budget.monthly`) | Billing → Reports, grouped by service |

Auto-close: API down, errors, restarts, queue, SQL after 30 minutes without a new breach; given up and redaction
after 24 hours.

### Log-based metrics

All three read the `worker` and `redaction` containers' JSON logs (`workers/jobs/JobRunner.cs` writes the messages):

| Metric | Counts | Label | Used by |
| --- | --- | --- | --- |
| `worker_jobs_given_up` | Lines containing `failed permanently` | `kind` (`jsonPayload.State.Kind`) | Alert "Worker gave up on a job", dashboard |
| `worker_job_failures` | Lines containing `failed on attempt` (each retried failure) | `kind` | Dashboard only (no alert: retries are normal; a burst usually means a provider is down) |
| `worker_loop_errors` | Lines containing `Job loop error` | none | Alert "Worker cannot reach the queue" |

### Uptime check

One: "API health (<env>)", `GET https://<api_domain>/v1/health` every 60 s, 10 s timeout, TLS validated, from all
the default checker regions. The web apps' hostnames have no uptime check (`deploy.sh` checks their `/healthz` once
after each deploy).

### Dashboard

"Fandom Score (<env>)", Terraform output `dashboard_url` (beta:
`https://console.cloud.google.com/monitoring/dashboards/builder/59585b13-d112-474c-89b9-97c25deadfcc?project=project-ef36a8a9-d958-4163-b4c`).
Tiles: requests per second by response class, latency p50/p95/p99 (load balancer); running containers per workload;
CPU (share of request) and memory (share of limit) per pod; worker failed attempts and given-up jobs; container
restarts; health check regions failing; Cloud SQL CPU/memory/disk and connections; recent errors from the API and
worker logs.

Known cosmetic diff: every `terraform plan` shows an in-place update to `google_monitoring_dashboard.main`, because
the API fills in default `targetAxis`/`xPos`/`yPos` values that `dashboard.tf` omits. Applying it changes nothing;
ignore it when reading a plan (or add the defaults to the JSON to silence it).

### Logs

Cloud Logging, project `$PROJECT`, Logs Explorer (or `gcloud logging read '<filter>' --project $PROJECT --freshness 1h`).
Retention: 30 days (`_Default`), admin audit logs 400 days (`_Required`); no exports. The API, worker, and migrator
log JSON, so `jsonPayload.Message`, `jsonPayload.LogLevel`, `jsonPayload.Category`, and `jsonPayload.State.*` are
queryable. The load balancer logs every API request (sample rate 1.0) and 10 % of web app requests
(`GCPBackendPolicy` in `k8s/base/gateway.yaml`, `web.yaml`). Every filter below was run against beta on 2026-11-03.

```
# API
resource.type="k8s_container" resource.labels.namespace_name="fandomscore" resource.labels.container_name="api"
# Worker and redaction (one image, two Deployments)
resource.type="k8s_container" resource.labels.namespace_name="fandomscore" resource.labels.container_name=("worker" OR "redaction")
# Migrations (the migrate Job of each deploy)
resource.type="k8s_container" resource.labels.namespace_name="fandomscore" resource.labels.container_name="migrate"
# Errors from any workload
resource.type="k8s_container" resource.labels.namespace_name="fandomscore" (severity>=ERROR OR jsonPayload.LogLevel="Error")
# Jobs given up, by kind
resource.type="k8s_container" resource.labels.container_name=("worker" OR "redaction") jsonPayload.Message:"failed permanently"
# Load balancer: all requests, and 5xx
resource.type="http_load_balancer"
resource.type="http_load_balancer" httpRequest.status>=500
# Cloud Armor denials (WAF 403s and rate-limit 429s); enforcedSecurityPolicy.priority says which rule
resource.type="http_load_balancer" jsonPayload.enforcedSecurityPolicy.outcome="DENY"
# Kubernetes events (scheduling, probes, image pulls)
logName="projects/<project>/logs/events" resource.labels.namespace_name="fandomscore"
# Cloud SQL (PostgreSQL server log)
resource.type="cloudsql_database"
# Who changed what (backups, restores, IAM, secrets): admin audit log, 400 days
logName:"cloudaudit.googleapis.com%2Factivity" protoPayload.serviceName="cloudsql.googleapis.com"
```

From a terminal: `kubectl logs deployment/api` (one pod), `kubectl logs job/migrate`, `kubectl get events
--sort-by=.lastTimestamp`.

## Incidents

1. **Acknowledge** the alert email and say in the team channel that you're on it.
2. **Stabilise before diagnosing**: if a deploy went out in the last hour, roll it back first (procedure 1).
3. **Protect data**: if something is corrupting data, stop the writers
   (`kubectl scale deployment/api deployment/worker deployment/redaction --replicas=0`) and note the time; that time is
   your restore point (procedure 3).
4. **Fix**, using `RUNBOOK.md` for the alert and this page for the procedure.
5. **Write it down** in `docs/incidents/<date>-<slug>.md`: timeline (UTC), alert, cause, what was done, data lost (if
   any, with the restore point), and follow-ups as TODO items (an alert, a dashboard tile, a runbook line).

## Gaps (known, not fixed)

Found while writing this page (2026-11-03); each needs a decision or a Terraform change, tracked here until then.

- **Instance deletion loses every backup.** `retainBackupsOnDelete` is false and no final backup is configured;
  beta also has Terraform `deletion_protection = false` and the instance flag `deletionProtectionEnabled` off. Prod's
  `sql.tf` doesn't set `settings.deletion_protection_enabled` either, so only `terraform destroy` is guarded there.
- **Pre-deploy backups are never pruned.** On-demand backups don't count toward the 14/30 retention (37 on beta
  after 8 days). They cost storage and keep deleted accounts' data in backups indefinitely, which contradicts the
  "at most ~30 + 30 days" statement in `services/api/src/features/privacy/DATA_INVENTORY.md`.
- **The archive's 30-day noncurrent rule is not applied on beta** (`docs/OWNER_TODO.md` item 9).
- **No cross-region copy of files**; buckets and the cluster are `us-central1` only.
- **Restoring onto a clone (variant B) can't be expressed in Terraform** without changing `sql.tf` (fixed instance
  name), and `deploy.sh` backs up `fandomscore-<env>` by name.
- **No alert for**: the worker or redaction Deployment having no running pod (scaled to zero, unschedulable) or jobs
  waiting too long in the queue; Cloud SQL disk usage or the instance being down (only indirectly, via the worker
  loop and 5xx alerts); a failed Cloud SQL backup; the web apps' hostnames (no uptime check); certificate problems
  (only once the API is down); a failed deploy (only GitHub's own workflow-failure email).
- **The uptime check doesn't reach the database** (`/v1/health` is a liveness check), so a database outage shows
  only as worker loop errors and 5xx.
- **`Spotify__TokenKey` has no copy outside Secret Manager**; deleting the secret makes all stored Spotify tokens
  unreadable.
- **No Data Access audit logs** (Secret Manager, Cloud Storage, Cloud SQL): after a suspected leak there is no record
  of who read a secret or a file.
- **Nothing in this page has been rehearsed except deploys**, notably the database restore (owner step,
  `docs/OWNER_TODO.md` section E).
