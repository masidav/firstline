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
| GCP access from pods | **Workload Identity Federation for GKE**, one Google service account per workload | No key files. Only `redaction` can read the quarantine bucket. |
| Files | **Cloud Storage**: `quarantine` (short retention, redaction only) and `archive` (redacted files only) | Enforces the schema's "never archive unredacted files" rule at the access level too. |
| Ingress | **Gateway API** with Google-managed certificates (Certificate Manager) | Standard Kubernetes ingress model on GKE. |
| Edge protection | **Cloud Armor** (WAF rules, rate limiting, bot management), optionally reCAPTCHA Enterprise on registration | Bots are the product's main adversary; on-sale registration must be defended at the edge. |
| Cache/queue | Memorystore for Redis only if the job library or rate limiting needs it | Postgres-backed jobs may make it unnecessary (ADR 0001 open item). |
| Infrastructure as code | **Terraform** for GCP resources; **Kustomize** overlays for Kubernetes manifests | Reviewable, repeatable environments. |
| CI/CD | GitHub Actions (or Cloud Build) using Workload Identity Federation; staging auto-deploys, prod needs approval | No long-lived cloud credentials in CI. |
| Observability | Cloud Logging (the API already logs JSON), Cloud Monitoring, OpenTelemetry traces to Cloud Trace, uptime checks | Managed and already integrated with GKE. |

## Environments
`dev`, `staging`, `prod` as separate GCP projects. Staging mirrors prod at a smaller size and holds only
synthetic data, so UI tests and load tests can run against it safely.

## On-sale traffic
Allocation registration opens at announced times, so traffic arrives in sharp spikes:
- Pre-scale the API and database before each scheduled registration window. Autoscaling alone reacts too slowly.
- Use HorizontalPodAutoscalers for normal load, and PodDisruptionBudgets so upgrades don't drop capacity.
- Keep registration writes small and idempotent. Run selection as a background job after the window closes,
  not on the request path.
- Load-test the expected peak with k6 against staging before every pilot (see the UI testing and launch plan).

## Consequences
- Running Cloud SQL with high availability roughly doubles the database cost. Use it in prod only.
- Kubernetes adds operational overhead compared with Cloud Run. GKE was chosen for control over workloads,
  networking, and scaling during on-sales. Cloud Run stays a fallback for small, stateless pieces.
- The Dockerfile, manifests, and Terraform live in `Dockerfile` and `infrastructure/` (see its README); applying them to real projects is tracked in `infrastructure/TODO.md`.
