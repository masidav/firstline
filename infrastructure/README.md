# Infrastructure and deployment

Three environments (ADR 0002, amended 2026-10-24):

| Environment | Where | Data | How it changes |
| --- | --- | --- | --- |
| **dev** | Your machine, `127.0.0.1`: docker-compose PostgreSQL, `dotnet run` for the API and worker, the fan app in the browser. | Fake, from `tools/dev/seed.py` (fans, artists, shows, allocations, registrations). Wipe and re-seed any time. | You, locally. `docs/DEVELOPMENT.md`. |
| **beta** | GCP project `fandomscore-beta`: GKE Autopilot, Cloud SQL, Secret Manager, Cloud Storage, external load balancer with Cloud Armor. | Real beta testers. Never seeded, never wiped. | Every push to `main` that passes CI deploys here, with a Cloud SQL backup before migrations. |
| **prod** | GCP project `fandomscore-prod`, same shape, bigger (HA database, more replicas, longer backups). | Real users. | The images that ran on beta are promoted after a manual approval. |

Terraform creates the cloud resources (one root module per cloud environment, one shared module); Kustomize
manifests describe the workloads; GitHub Actions builds images and deploys.

| Path | What |
| --- | --- |
| `../Dockerfile` | One multi-stage build with targets `api`, `worker`, `migrate` (chiseled, non-root). |
| `terraform/modules/environment` | Everything one cloud environment needs (network, cluster, database, registry, secrets, buckets, edge, CI identity, alerts). |
| `terraform/envs/{beta,prod}` | Root module per cloud environment: sizes, protection, budget. |
| `terraform/bootstrap.sh` | One-time project setup: billing link, base APIs, Terraform state bucket. |
| `k8s/base` | Namespace, service accounts, migrate Job, API and worker Deployments, HPA, PDB, Gateway. |
| `k8s/overlays/{beta,prod}` | Project, images, hostnames, secrets list, replica counts. |
| `k8s/deploy.sh` | Backup, migrations, rollout, health check. Used by CI and by hand. |
| `../.github/workflows/deploy.yml` | Push to `main`: build, deploy beta, promote to prod after approval. |
| `../tools/ci/check_migration_safety.py` | CI: a new migration must keep the previous release working during the rollout. |
| `../tools/dev/seed.py` | dev only: fills the local database with fake data. |
| `docker-compose.yml` | Local PostgreSQL (dev). |

## Changing beta (and prod) without breaking them

Beta has real users, so the deploy pipeline is built around "the previous version keeps working at every step":

1. **CI first.** A push to `main` runs build, tests, the migration checks, the seed script, and the app checks.
   The deploy workflow runs independently, but a failed deploy of a broken commit is caught by the migrate Job
   or the health check below; fix forward or roll back (don't merge red).
2. **Migrations are additive.** `deploy.sh` runs a commit's migrations *before* its pods take traffic, and a
   rollback is the previous application version against the current schema. So a migration may add tables,
   columns (nullable or with a default), indexes, functions, and data, but may not drop, rename, or tighten what
   the previous release uses. Remove things in a later migration, once no deployed version reads them
   (expand, release, contract). CI enforces this for every new migration (`check_migration_safety.py`); a
   contract-step migration opts out with a comment line `-- allow-breaking: <what stopped using it, and when>`.
3. **A backup before every migration.** `deploy.sh` takes an on-demand Cloud SQL backup (`pre-deploy <sha>`)
   before the migrate Job (the deploy service account has a custom role with only backup permissions).
   Point-in-time recovery covers the last seven days on top of that.
4. **Migrate, then roll out, then check.** The migrate Job must complete (its log is printed; a failure stops
   the deploy with the old pods still serving), then the Deployments roll (surge, with a PodDisruptionBudget),
   then the public health endpoint is polled for a minute. A failing health check fails the deploy, which is
   the signal to roll back.
5. **Prod gets what beta ran.** The prod deploy copies beta's image digests into the prod registry; nothing is
   rebuilt. It waits for the approval on the `prod` GitHub environment, and its Workload Identity binding only
   accepts tokens from that environment, so the approval can't be skipped.

What this doesn't cover: a migration that is additive but slow (a new index on a big table locks it; use
`CREATE INDEX CONCURRENTLY` in its own migration), and application bugs that pass health. For those, deploy beta,
use it, then approve prod.

## How a request and a secret get in

- Client → Google external Application Load Balancer (static address, Google-managed certificate, Cloud Armor:
  WAF rules, per-IP throttle, adaptive DDoS protection) → `api` Service → pods. The API trusts
  `X-Forwarded-For` only from the Google Front End ranges and the load balancer's own address
  (`ForwardedHeaders__KnownNetworks`, `ForwardedHeaders__KnownProxies`), so the sign-in rate limit sees the
  real client.
- Secrets never leave Secret Manager as environment variables: the GKE Secret Manager add-on mounts each one as a
  file named after its configuration key (`/secrets/Auth__SigningKey`), and the processes read that directory
  (`Secrets__Directory`). Each workload has its own Kubernetes service account bound through Workload Identity
  to a Google service account that can read only its secrets (`secret_readers` in the Terraform module).
- Only the `redaction` identity can read the quarantine bucket; the API can only write to it and read the
  archive. The archive keeps versions. Nothing uses service-account key files.

## Set up a cloud environment (once per environment)

Needs `gcloud`, `terraform` (>= 1.9), `kubectl`, `kustomize`, Owner on the project, and Billing Account
Administrator (or Costs Manager) on the billing account for the budget alert. Do beta first; prod is the same
steps with `prod` in place of `beta`.

1. **Project.** `gcloud projects create fandomscore-beta` (or whatever `project_id` you choose), then
   `infrastructure/terraform/bootstrap.sh fandomscore-beta XXXXXX-XXXXXX-XXXXXX us-central1`. It links billing,
   enables the APIs Terraform needs to start, and creates the state bucket `<project>-tfstate`.
2. **Terraform.**
   ```bash
   cd infrastructure/terraform/envs/beta
   cp terraform.tfvars.example terraform.tfvars    # project, region, api_domain, billing account, alert email
   terraform init -backend-config="bucket=fandomscore-beta-tfstate"
   terraform apply
   ```
   The first apply takes about 20 minutes (cluster and database). It prints the outputs the next steps need.
   The database password is generated by Terraform and stored only in Secret Manager
   (`ConnectionStrings__Database`) and the Terraform state, which is why the state bucket is private.
3. **DNS.** Create the CNAME from output `certificate_dns_record` (lets Certificate Manager issue the
   certificate) and an A record for `api_domain` pointing at `api_ip_address`. The certificate becomes active
   within an hour of the CNAME resolving.
4. **Secrets.** Add a version to each secret Terraform created (values as in `.env.example`):
   ```bash
   openssl rand -base64 32 | tr -d '\n' | gcloud secrets versions add Auth__SigningKey --project fandomscore-beta --data-file=-
   openssl rand -base64 32 | tr -d '\n' | gcloud secrets versions add Spotify__TokenKey --project fandomscore-beta --data-file=-
   printf '%s' "$SPOTIFY_CLIENT_SECRET" | gcloud secrets versions add Spotify__ClientSecret --project fandomscore-beta --data-file=-
   gcloud secrets versions add Auth__Apple__PrivateKey --project fandomscore-beta --data-file=AuthKey_XXXX.p8
   ```
   Every secret listed in `k8s/overlays/<env>/secrets.yaml` needs a version, or the pods cannot start.
   `Spotify__TokenKey` must be the same value for the API and the worker (it is: one secret, two readers).
   Beta and prod use different Spotify apps and Apple keys, so a beta tester never touches prod credentials.
5. **Overlay.** In `k8s/overlays/<env>/`, set the project id and region (`kustomization.yaml`,
   `serviceaccounts.yaml`, `secrets.yaml`), the hostname (`gateway.yaml`), `ForwardedHeaders__KnownProxies` to
   output `api_ip_address`, and the non-secret provider settings (`Auth__*__ClientIds`, `Spotify__ClientId`,
   `Spotify__RedirectUris`, `Cors__AllowedOrigins`). Commit these; they hold no secrets.
6. **GitHub.** Create the environment (`beta` or `prod`) under repository settings with the
   variables `GCP_PROJECT_ID`, `GCP_REGION`, `GCP_WIF_PROVIDER` (output `workload_identity_provider`), and
   `GCP_DEPLOY_SA` (output `deploy_service_account`). Add required reviewers on `prod`. The Workload Identity
   provider only accepts tokens from this repository running in the GitHub environment with the same name, so
   a prod deploy cannot skip the approval. Finally set the repository variable `DEPLOY_ENABLED` to `true`: the
   Deploy workflow is skipped without it, so pushes stay green before the environments exist.
7. **First deploy.** Push to `main`, or run the Deploy workflow by hand (`beta`). Or from a machine with the tools
   (the first deploy of an empty database needs no backup, and the health check needs DNS and the certificate):
   ```bash
   gcloud container clusters get-credentials fandomscore-beta --region us-central1 --project fandomscore-beta
   gcloud auth configure-docker us-central1-docker.pkg.dev
   for t in api worker migrate; do
     docker build --target $t -t us-central1-docker.pkg.dev/fandomscore-beta/fandomscore/$t:$(git rev-parse HEAD) . && docker push "$_"
   done
   SKIP_BACKUP=1 SKIP_HEALTH_CHECK=1 infrastructure/k8s/deploy.sh beta $(git rev-parse HEAD)
   ```
   Then `curl https://api.beta.example.com/v1/health`.

## Deploying

`deploy.sh <beta|prod> <tag>` sets the three image tags in the overlay, takes the Cloud SQL backup, applies
everything except the Deployments, deletes the previous `migrate` Job and creates a new one, waits for it to
complete (prints its log, fails the deploy if it fails), applies the Deployments, waits for both rollouts, and
polls the public health endpoint. Migrations run once per commit before any new API pod receives traffic; the
Job's image is the same commit as the API.

- **Promotion.** Push to `main` builds the images once (tagged with the commit SHA), deploys beta, and after
  approval copies the same digests into the prod registry and deploys prod. Running the workflow by hand with
  `beta` stops after beta; with `prod` it does both.
- **Roll back.** Migrations are forward-only (see above), so a rollback is the previous application version
  against the current schema. Redeploy the previous commit: `deploy.sh prod <previous-sha>` (the images are still
  in the registry; the migrate Job finds nothing to do; `SKIP_BACKUP=1` is fine, nothing migrates), or for an
  immediate stop-gap `kubectl rollout undo deployment/api -n fandomscore`.
- **A migration broke data.** Restore the `pre-deploy <sha>` backup. A restore replaces the instance's data, so
  stop the API and worker first (`kubectl scale deployment/api deployment/worker --replicas=0 -n fandomscore`),
  then `gcloud sql backups list --instance fandomscore-beta` and `gcloud sql backups restore <id> --restore-instance
  fandomscore-beta`, redeploy the previous commit, and scale back up. Writes made after the backup are lost;
  for a narrower window, clone to a point in time instead (Backups and restore). On prod, prefer the clone.
- **Config change without a code change.** Edit the overlay and run `deploy.sh` with the current SHA. The
  ConfigMap name carries a content hash, so pods restart with the new values.
- **Secret rotation.** Add a new secret version; restart the Deployment (`kubectl rollout restart deployment/api
  -n fandomscore`) because the mount is read at start. For `Spotify__TokenKey`, follow `.env.example`: add
  `Spotify__PreviousTokenKeys` as a secret (`gcloud secrets create`), grant the api and worker identities access,
  add it to both `SecretProviderClass` lists, deploy, and remove it after every user has synced.

## On-sale traffic

The HPA scales the API on CPU but reacts in minutes; registration windows open in seconds. Before a scheduled
on-sale:

1. Raise the floor an hour ahead: `kubectl patch hpa api -n fandomscore -p '{"spec":{"minReplicas":10}}'`
   (Autopilot provisions capacity for the new pods). Put it back afterwards.
2. Check Cloud SQL headroom (CPU, connections: each API pod holds up to 20). Changing the tier restarts the
   instance, so do it the day before, in the maintenance window if possible.
3. Load-test first: k6 against a local stack seeded with `tools/dev/seed.py --fans 20000`, or against beta
   outside testers' hours with a throwaway artist (see `docs/plans/ui-testing-and-launch.md`).

## Backups and restore

Cloud SQL takes a daily backup at 02:00 UTC, `deploy.sh` takes one before each migration, and transaction logs
are kept for seven days, so any point in that window can be restored. The safest restore is a new instance:

```bash
gcloud sql instances clone fandomscore-prod fandomscore-prod-restore \
  --point-in-time 2026-10-21T08:30:00Z --project fandomscore-prod
```

Then verify the data, write a new `ConnectionStrings__Database` version pointing at the clone's private address,
restart the API and worker, and later delete the old instance (it has deletion protection; Terraform will see the
drift, so update `sql.tf` or import the clone). Rehearse this on beta and record the result in `TODO.md`.

## Looking at it

- Logs: Cloud Logging, `resource.type="k8s_container" resource.labels.namespace_name="fandomscore"`. The API and
  worker log JSON, so fields are queryable. Locally: `kubectl logs deployment/api -n fandomscore`.
- Health: `https://<api_domain>/v1/health`, checked every minute from several regions; the alert policy emails
  `alert_email` after five minutes of failures. Budget alerts go to the same address at 50 %, 90 %, 100 % and when
  the forecast exceeds 100 %.
- Images: Artifact Registry scans each pushed image; findings are on the image in the console.
- Database: Query Insights is on for the Cloud SQL instance.

## Known limitations

- Nothing here has been applied to a real project yet: Terraform passes `validate` and the manifests build, but
  the first `apply` and deploy will surface anything the provider rejects at runtime.
- Stored photos have no cluster storage yet. `Storage__LocalPath` (local disk) is for development; in the cluster the
  root filesystem is read-only and the API and worker pods share no volume. Both processes must use the **same**
  storage, because the API writes files and the worker's `storage.cleanup` job deletes them: put any `Storage__*`
  setting in the shared `app-config` ConfigMap, not one Deployment, until the Cloud Storage `IFileStore` replaces it.
- The worker has no health probe (it serves no HTTP). A stuck worker is visible through the job queue
  (`jobs` table) and logs, not through Kubernetes.
- The `SecretProviderClass` lists in each overlay duplicate the `secret_readers` map in Terraform; change both.
- The migration safety check is a pattern match, not a parser: it knows the common breaking statements and
  ignores function bodies. Slow-but-additive migrations (indexes on big tables) are on the author.
- No promoter dashboard deployment yet (`apps/promoter-web` is not built).
