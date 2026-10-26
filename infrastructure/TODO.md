# TODO — Infrastructure and deployment

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect current Docker, hosting, deployment, and environment setup before adding new tooling. (2026-10-21: only `docker-compose.yml` existed.)
- [ ] Provide documented local development services for the selected database, cache, and object storage. (Database: `docker-compose.yml`. No cache; object storage pending the submissions feature.)
- [x] Create .env.example with variable names and safe placeholders only; never add real secrets.
- [x] Keep production secrets in the chosen secret manager and out of source control and logs. (Secret Manager, mounted as files; `Secrets__Directory`. Values are added with gcloud, never through Terraform except the database connection string.)
- [x] Configure persistent storage and access policies for quarantine and archive objects. (Terraform `storage.tf`: quarantine readable only by `redaction`, 30-day deletion; archive versioned, redaction writes, API reads. Application code for uploads is still the submissions feature's work.)
- [ ] Configure background worker execution, graceful shutdown, retry policies, and dead-letter/failure visibility. (Worker Deployment with a 60 s grace period; retries in the queue, `workers/README.md`. 2026-10-25: a job that fails its last attempt is logged at Error as `failed permanently`; log-based metric `worker_jobs_given_up` and the "Worker gave up on a job" alert watch that line, `monitoring.tf`. Open until the alert is seen firing on beta.)
- [ ] Run redaction (`submissions.redact`, `redaction.sweep`, and `storage.cleanup`'s quarantine deletes) in a
      separate `redaction` Deployment with the `redaction` service account once the Cloud Storage file store exists:
      only it may read and delete quarantine objects. Today they run in the shared worker on local disk.
- [x] Alert when verified proof is still unarchived 25 days after upload (`monitoring.tf` `redaction_overdue`, a
      log-match alert on the sweep's warning). `terraform validate` (beta) 2026-10-26; not applied.
- [ ] Add health checks and basic application, worker, database, and queue monitoring. (API probes, uptime check, and alert exist. 2026-10-25: worker liveness endpoint `GET /healthz` on port 8081 backed by per-loop heartbeats, with a liveness probe in the Deployment; verified by `tests/FandomScore.Tests/WorkerHealthTests.cs` (4 tests: alive/stale rules, 200 then 503 over HTTP with a fake clock, real loops reporting) and a smoke test of the `worker` image against a throwaway PostgreSQL container (200 with both loops reporting, still 200 with the database stopped; the 503 path is only covered by the unit test, since pausing the process also pauses the endpoint). The chiseled image logs `Cannot load library libgssapi_krb5.so.2` once at startup (Npgsql probing GSSAPI); harmless with password authentication. Alerts for pods restarting, worker loop errors, Cloud SQL CPU/memory, API 5xx ratio, and a dashboard (`dashboard.tf`): written and `terraform validate`d, unverified until the first apply.)
- [x] Document database backup and restore procedures. (`README.md`, Backups and restore. Restore not rehearsed yet.)
- [x] Document deployment, migration, rollback, and recovery procedures. (`README.md`.)
- [ ] Verify mobile build configuration and required platform environment variables.
- [ ] Review production privacy, access control, retention, and cost implications before release.

## Environments (decided 2026-10-24)
- [x] Three environments: **dev** on the developer's machine (127.0.0.1, docker-compose database, fake data), **beta**
      on GCP with real beta testers (every push to `main` lands there), **prod** promoted from beta after approval.
      The GCP `dev` and `staging` roots and overlays were removed; `staging` became `beta`. Verified 2026-10-24:
      `terraform validate` (beta, prod; google provider 6.50 in Docker), `kustomize build` of both overlays.
- [x] Dev seed script `tools/dev/seed.py`: fans with hub cities, signals, journal logs (ledger points through the real
      triggers), verified submissions, scores; active artists with catalogs; shows with allocations in every state and
      registrations through `register_for_allocation()`. Verified 2026-10-24 against a scratch database (200 fans, 8
      artists, 10 shows: 230 registrations, 46 withdrawn; and 50/3/6 with another seed); refuses to run twice.
      CI runs it against PostgreSQL 16 on every push (`ci.yml`, job `seed`).
- [x] Beta can take changes without breaking: `deploy.sh` backs up Cloud SQL before the migrate Job (custom IAM role
      with backup permissions only), migrates before the rollout, and fails the deploy if the health endpoint doesn't
      answer; CI blocks new migrations that drop, rename, or tighten what the running release uses
      (`tools/ci/check_migration_safety.py`, 8 unit tests, passes on all existing migrations). `README.md`
      "Changing beta (and prod) without breaking them".
- [ ] Rehearse the "a migration broke data" restore on beta (`README.md`, Deploying) and record the result.

## GCP / GKE (Phase 12, see docs/adr/0002-hosting-gcp-gke.md)
Written 2026-10-21 and verified locally (`terraform validate` with google provider 6.50, `kustomize build` of the
overlays, Docker builds of the three images, `dotnet test`). Not yet applied to a GCP project: the steps in
`README.md` "Set up a cloud environment" are what remains for beta and prod.
- [ ] Create the beta and prod GCP projects with billing budgets and alerts. (2026-10-25: beta is the existing project `project-ef36a8a9-d958-4163-b4c` (display name firstline, number 494880048811); its Google OAuth client ID is in the beta overlay. `bootstrap.sh` run against it: billing linked, base APIs enabled, state bucket `project-ef36a8a9-d958-4163-b4c-tfstate` created. `terraform init` against that bucket and a `plan` with placeholder domain succeed: 95 resources to add, no provider errors. `terraform apply` run 2026-10-26: 95 resources, after fixing three things the first attempt surfaced (Cloud SQL edition, Workload Identity pool ordering, API enablement race; the GKE creation also hit a transient `us-central1` stockout and succeeded on retry). API address 136.81.191.137; cluster `fandomscore-beta` RUNNING with pool `project-ef36a8a9-d958-4163-b4c.svc.id.goog`. Prod not created.) (Budget and alert channel are in Terraform `monitoring.tf`; the projects themselves are created by hand, `bootstrap.sh`.)
- [x] Terraform: VPC, GKE Autopilot cluster, Cloud SQL PostgreSQL 16 (private IP, PITR, HA in prod), Artifact Registry, Secret Manager, buckets. (Applied to beta 2026-10-26, `terraform apply` complete with 95 resources; prod pending. 2026-10-25: the connection string now verifies the server certificate, `SSL Mode=VerifyCA` with the instance's server CA stored by Terraform as `Database__ServerCa` and mounted next to it; the earlier `SSL Mode=Require` would have failed, since Npgsql 6+ validates certificates.)
- [x] Dockerfiles for the API, migrator, and workers (non-root, minimal base images); scan images in Artifact Registry. (`Dockerfile` targets; chiseled runtime images; `containerscanning` API enabled. Built locally 2026-10-21.)
- [x] Kustomize base and per-environment overlays; migrations as a pre-deploy Kubernetes Job. (`k8s/`, `deploy.sh`; builds for beta and prod.)
- [ ] Workload Identity per workload; quarantine bucket readable only by the redaction workload. (In Terraform and the manifests; verify after the first apply.)
- [ ] Gateway API ingress with Google-managed certificates and Cloud Armor policies. (`k8s/base/gateway.yaml`, `terraform/.../edge.tf`; verify after the first apply.)
- [x] Configure forwarded headers in the API to trust only the load balancer, so per-IP rate limits (sign-in) see the real client IP. (`ForwardedHeadersSetup`; `ForwardedHeadersTests` pass.)
- [ ] HorizontalPodAutoscalers, PodDisruptionBudgets, resource requests, and a pre-scale procedure for scheduled on-sales. (Manifests and `README.md` procedure exist; untested against a cluster.)
- [ ] CI/CD with Workload Identity Federation (no long-lived keys); beta deploys automatically, prod needs approval. (`.github/workflows/deploy.yml`; runs once the GitHub environments have their variables.)
- [ ] Cloud Logging and Monitoring dashboards, uptime checks, alerts, and an on-call runbook. (2026-10-25: dashboard in `dashboard.tf`, seven alert policies in `monitoring.tf`, and `RUNBOOK.md`. Uptime check since 2026-10-21. Open until the dashboard shows data and the alerts are exercised on beta.)
- [x] CI keeps the hosting code valid before it is applied: `terraform fmt -check` and `validate` for beta and prod, `kustomize build` of both overlays, and a build of the three images (`ci.yml`, job `infrastructure`). Added 2026-10-25; the same commands were run locally in Docker (terraform 1.9.8, kustomize 5.6.0, image builds) before the push.
- [ ] Rehearse a Cloud SQL point-in-time restore and record the result.

## Verification
- [x] Add or update automated tests for the behavior above. (`tests/FandomScore.Tests/ForwardedHeadersTests.cs`.)
- [x] Run the relevant tests and record actual results. (See the note above the GCP list.)
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
