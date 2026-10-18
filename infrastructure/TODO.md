# TODO — Infrastructure and deployment

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Inspect current Docker, hosting, deployment, and environment setup before adding new tooling.
- [ ] Provide documented local development services for the selected database, cache, and object storage.
- [ ] Create .env.example with variable names and safe placeholders only; never add real secrets.
- [ ] Keep production secrets in the chosen secret manager and out of source control and logs.
- [ ] Configure persistent storage and access policies for quarantine and archive objects.
- [ ] Configure background worker execution, graceful shutdown, retry policies, and dead-letter/failure visibility.
- [ ] Add health checks and basic application, worker, database, and queue monitoring.
- [ ] Document database backup and restore procedures.
- [ ] Document deployment, migration, rollback, and recovery procedures.
- [ ] Verify mobile build configuration and required platform environment variables.
- [ ] Review production privacy, access control, retention, and cost implications before release.

## GCP / GKE (Phase 12, see docs/adr/0002-hosting-gcp-gke.md)
- [ ] Create dev, staging, and prod GCP projects with billing budgets and alerts.
- [ ] Terraform: VPC, GKE Autopilot cluster, Cloud SQL PostgreSQL 16 (private IP, PITR, HA in prod), Artifact Registry, Secret Manager, buckets.
- [ ] Dockerfiles for the API, migrator, and workers (non-root, minimal base images); scan images in Artifact Registry.
- [ ] Kustomize base and per-environment overlays; migrations as a pre-deploy Kubernetes Job.
- [ ] Workload Identity per workload; quarantine bucket readable only by the redaction workload.
- [ ] Gateway API ingress with Google-managed certificates and Cloud Armor policies.
- [ ] HorizontalPodAutoscalers, PodDisruptionBudgets, resource requests, and a pre-scale procedure for scheduled on-sales.
- [ ] CI/CD with Workload Identity Federation (no long-lived keys); staging deploys automatically, prod needs approval.
- [ ] Cloud Logging and Monitoring dashboards, uptime checks, alerts, and an on-call runbook.
- [ ] Rehearse a Cloud SQL point-in-time restore and record the result.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
