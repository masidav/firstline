# ADR 0002: Host on Google Cloud with GKE

- **Status:** Accepted direction (2026-10-18). Component choices below are proposed until Phase 12 starts.
- **Scope:** API, migrator, workers, and the promoter web dashboard. The mobile app is not hosted: it ships
  through the App Store and Google Play and calls the API.

## Proposed architecture

| Concern | Choice | Why |
| --- | --- | --- |
| Cluster | **GKE Autopilot**, one regional cluster per environment | Google manages nodes and security patches; you pay per pod. Standard mode only if we need custom node types (e.g. GPUs for vision). |
| Database | **Cloud SQL for PostgreSQL 16**, private IP, high availability and point-in-time recovery in prod | Not in-cluster: managed backups, failover, and patching. Supports `citext`. |
| Workloads | `api` (Deployment), `worker` (Deployment), `redaction` (Deployment), `migrate` (Job before each rollout), `promoter-web` (Deployment) | Matches ADR 0001: one codebase, separate processes. |
| Images | **Artifact Registry** with vulnerability scanning | One registry per environment project or a shared one with per-environment access. |
| Secrets | **Secret Manager**, mounted with the GKE Secret Manager add-on | Matches the environment-variable-only configuration already in the API. |
| GCP access from pods | **Workload Identity Federation for GKE**, one Google service account per workload | No key files. Only `redaction` can read the quarantine bucket. *Amended 2026-10-26, see Files below.* |
| Files | **Cloud Storage**: `quarantine` (short retention, redaction only) and `archive` (redacted files only) | Enforces the schema's "never archive unredacted files" rule at the access level too. *Amended 2026-10-26, see Files below.* |
| Ingress | **Gateway API** with Google-managed certificates (Certificate Manager) | Standard Kubernetes ingress model on GKE. |
| Edge protection | **Cloud Armor** (WAF rules, rate limiting, bot management), optionally reCAPTCHA Enterprise on registration | Bots are the product's main adversary; on-sale registration must be defended at the edge. |
| Cache/queue | Memorystore for Redis only if the job library or rate limiting needs it | Postgres-backed jobs may make it unnecessary (ADR 0001 open item). |
| Infrastructure as code | **Terraform** for GCP resources; **Kustomize** overlays for Kubernetes manifests | Reviewable, repeatable environments. |
| CI/CD | GitHub Actions (or Cloud Build) using Workload Identity Federation; beta auto-deploys, prod needs approval | No long-lived cloud credentials in CI. |
| Observability | Cloud Logging (the API already logs JSON), Cloud Monitoring, OpenTelemetry traces to Cloud Trace, uptime checks | Managed and already integrated with GKE. |

## Environments
Amended 2026-10-24. Three environments, two of them in the cloud:
- **dev** runs on the developer's machine (`127.0.0.1`): docker-compose PostgreSQL, `dotnet run`, the app in the
  browser, and fake data from `tools/dev/seed.py`. There is no GCP dev project.
- **beta** is a GCP project with real beta testers. Every push to `main` deploys there after CI, with a Cloud SQL
  backup before migrations, additive-only migrations enforced by CI, and a health check after the rollout
  (`infrastructure/README.md`, "Changing beta (and prod) without breaking them").
- **prod** is a GCP project at full size; the images that ran on beta are promoted after approval.

The original plan had a synthetic-data `staging` project between dev and prod. It was dropped: the local seed
gives every developer a full data set, and a small group of real testers on beta finds what synthetic data
doesn't. Load tests run against a seeded local stack or against beta outside testers' hours.

## Files
Amended 2026-10-26, when Cloud Storage was wired up (`packages/storage/GcsFileStore.cs`). Three buckets, picked by the
storage key's prefix: `quarantine/` (unredacted uploads, deleted by the bucket at 30 days), `archive/` (redacted proof
only), and `media/` (staff-published media such as featured videos, which must outlive the quarantine expiry).

Access follows what the code does, which differs from the table above:
- Staff review unredacted proof and fans see their own pending photos, both through the API, and the API deletes
  quarantined files when a fan withdraws or staff discard one. So the API reads, writes, and deletes in quarantine.
- Redaction runs inside the worker (`workers/image-redaction`), not a separate deployment, so the worker reads
  quarantine and writes the archive. It also runs `storage.cleanup`, which deletes from all three buckets.
- So "only redaction can read quarantine" doesn't hold today: the API and the worker both can. The schema still
  refuses unredacted archive rows (migration 0029), the archive is written only by redaction code, and the API can
  only read it. Splitting redaction into its own deployment with its own identity would restore the stricter rule;
  the `redaction` service account and its bindings are kept for that.

Uploads go through the API, not straight to the bucket: the API strips photo metadata (GPS location included) before
anything is stored. Downloads keep the API's signed link, which redirects to a 15-minute Cloud Storage link signed
by the API's own identity (IAM signBlob, so no key files), so the API doesn't stream the bytes.

## On-sale traffic
Allocation registration opens at announced times, so traffic arrives in sharp spikes:
- Pre-scale the API and database before each scheduled registration window. Autoscaling alone reacts too slowly.
- Use HorizontalPodAutoscalers for normal load, and PodDisruptionBudgets so upgrades don't drop capacity.
- Keep registration writes small and idempotent. Run selection as a background job after the window closes,
  not on the request path.
- Load-test the expected peak with k6 against a seeded local stack, or beta outside testers' hours, before every pilot (see the UI testing and launch plan).

## Consequences
- Running Cloud SQL with high availability roughly doubles the database cost. Use it in prod only.
- Kubernetes adds operational overhead compared with Cloud Run. GKE was chosen for control over workloads,
  networking, and scaling during on-sales. Cloud Run stays a fallback for small, stateless pieces.
- The Dockerfile, manifests, and Terraform live in `Dockerfile` and `infrastructure/` (see its README); applying them to real projects is tracked in `infrastructure/TODO.md`.
