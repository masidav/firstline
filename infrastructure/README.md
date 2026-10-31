# Infrastructure and deployment

Three environments (ADR 0002, amended 2026-10-24):

| Environment | Where | Data | How it changes |
| --- | --- | --- | --- |
| **dev** | Your machine, `127.0.0.1`: docker-compose PostgreSQL, `dotnet run` for the API and worker, the fan app in the browser. | Fake, from `tools/dev/seed.py` (fans, artists, shows, allocations, registrations). Wipe and re-seed any time. | You, locally. `docs/DEVELOPMENT.md`. |
| **beta** | GCP project `project-ef36a8a9-d958-4163-b4c` (display name "firstline"; the cluster, database, and registry are still named `fandomscore-beta`): GKE Autopilot, Cloud SQL, Secret Manager, Cloud Storage, external load balancer with Cloud Armor. | Real beta testers. Never seeded, never wiped. | Every push to `main` that passes CI deploys here, with a Cloud SQL backup before migrations. |
| **prod** | GCP project `fandomscore-prod`, same shape, bigger (HA database, more replicas, longer backups). | Real users. | The images that ran on beta are promoted after a manual approval. |

Terraform creates the cloud resources (one root module per cloud environment, one shared module); Kustomize
manifests describe the workloads; GitHub Actions builds images and deploys.

| Path | What |
| --- | --- |
| `../Dockerfile` | One multi-stage build with targets `api`, `worker`, `migrate` (chiseled, non-root), and `web`, `promoter-web` (nginx). |
| `terraform/modules/environment` | Everything one cloud environment needs (network, cluster, database, registry, secrets, buckets, edge, CI identity, alerts). |
| `terraform/envs/{beta,prod}` | Root module per cloud environment: sizes, protection, budget. |
| `terraform/bootstrap.sh` | One-time project setup: billing link, base APIs, Terraform state bucket. |
| `k8s/base` | Namespace, service accounts, migrate Job, API, worker, and redaction Deployments, HPA, PDB, Gateway. |
| `k8s/overlays/{beta,prod}` | Project, images, hostnames, secrets list, replica counts. |
| `k8s/deploy.sh` | Backup, migrations, rollout, health check. Used by CI and by hand. |
| `k8s/db-shell.sh` | psql on an environment's database from a temporary pod (`db-shell.sh beta`); the password stays in the cluster. |
| `RUNBOOK.md` | On-call: what each alert means and what to do; first-five-minutes checklist; common actions. |
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
- The API builds every absolute link it hands out (signed file links, score cards, featured videos) from
  `App__PublicOrigin` (the overlay's `app-config`, e.g. `https://api.beta.firstline-5730kl.com`), never from the request's
  Host header, and refuses to start without it outside Development. It does not filter Host (`AllowedHosts` stays
  `*`): the kubelet probes and the load balancer's health checks send the pod address as Host and would be refused.
- Secrets never leave Secret Manager as environment variables: the GKE Secret Manager add-on mounts each one as a
  file named after its configuration key (`/secrets/Auth__SigningKey`), and the processes read that directory
  (`Secrets__Directory`). Each workload has its own Kubernetes service account bound through Workload Identity
  to a Google service account that can read only its secrets (`secret_readers` in the Terraform module).
- The database connection is TLS with the server certificate verified: Terraform stores the Cloud SQL instance's
  server CA as the secret `Database__ServerCa`, mounted next to the connection string, which refers to it
  (`SSL Mode=VerifyCA;Root Certificate=/secrets/Database__ServerCa`). Npgsql validates the certificate, so a
  connection string without the CA fails against Cloud SQL's per-instance CA.
- Bucket access (`storage.tf`, ADR 0002 "Files"). The worker image runs as two Deployments: `redaction` runs only
  `submissions.redact`, `redaction.sweep`, and `storage.cleanup` (`Jobs__OnlyKinds`) under the `redaction`
  identity, which reads, writes, and deletes in the quarantine, archive, and media buckets; `worker` runs every
  other job (`Jobs__ExceptKinds`, the same three kinds) and has no bucket access at all, so the jobs that call
  Spotify, Ticketmaster, and Expo can't reach a single photo. The API reads, writes, and deletes in quarantine and
  media and reads the archive. The archive keeps versions. Nothing uses service-account key files.
- Trade-off, chosen to keep the app as it is: the API still reads quarantine, because staff review unredacted
  proof and fans see their own pending photo through it (`GET /v1/files/{id}`, a redirect to a link the API signs).
  So "only redaction can read quarantine" holds among the workers, not for the API. Taking it from the API would
  need staff review and the pending-photo preview to go through another service (or links signed by the redaction
  identity); not worth it while the API is also where uploads arrive.

## Set up a cloud environment (once per environment)

Needs `gcloud`, `terraform` (>= 1.9), `kubectl`, `kustomize`, Owner on the project, and Billing Account
Administrator (or Costs Manager) on the billing account for the budget alert. Do beta first; prod is the same
steps with `prod` in place of `beta`.

1. **Project.** Beta is the existing project `project-ef36a8a9-d958-4163-b4c` (shown as "firstline" in the console); prod is `gcloud projects create fandomscore-prod` (or
   whatever `project_id` you choose). Then `infrastructure/terraform/bootstrap.sh project-ef36a8a9-d958-4163-b4c XXXXXX-XXXXXX-XXXXXX us-central1`. It links billing,
   enables the APIs Terraform needs to start, and creates the state bucket `<project>-tfstate`.
2. **Terraform.**
   ```bash
   cd infrastructure/terraform/envs/beta
   cp terraform.tfvars.example terraform.tfvars    # project, region, api_domain, billing account, alert email
   terraform init -backend-config="bucket=project-ef36a8a9-d958-4163-b4c-tfstate"
   terraform apply
   ```
   The first apply takes about 20 minutes (cluster and database). It prints the outputs the next steps need.
   The database password is generated by Terraform and stored only in Secret Manager
   (`ConnectionStrings__Database`) and the Terraform state, which is why the state bucket is private.
3. **DNS.** Create the CNAME from output `certificate_dns_record` (lets Certificate Manager issue the
   certificate) and an A record for `api_domain` pointing at `api_ip_address`. For the web apps ("Web apps"
   below), also create each CNAME in output `web_certificate_dns_records` and an A record for each hostname in
   `web_domains` pointing at the same `api_ip_address`. Each certificate becomes active within an hour of its
   CNAME resolving.
4. **Secrets.** Add a version to each secret Terraform created (values as in `.env.example`):
   ```bash
   openssl rand -base64 32 | tr -d '\n' | gcloud secrets versions add Auth__SigningKey --project project-ef36a8a9-d958-4163-b4c --data-file=-
   openssl rand -base64 32 | tr -d '\n' | gcloud secrets versions add Spotify__TokenKey --project project-ef36a8a9-d958-4163-b4c --data-file=-
   printf '%s' "$SPOTIFY_CLIENT_SECRET" | gcloud secrets versions add Spotify__ClientSecret --project project-ef36a8a9-d958-4163-b4c --data-file=-
   gcloud secrets versions add Auth__Apple__PrivateKey --project project-ef36a8a9-d958-4163-b4c --data-file=AuthKey_XXXX.p8
   printf '%s' "$TICKETMASTER_API_KEY" | gcloud secrets versions add Ticketmaster__ApiKey --project project-ef36a8a9-d958-4163-b4c --data-file=-
   ```
   Every secret listed in `k8s/overlays/<env>/secrets.yaml` needs a version, or the pods cannot start
   (`ConnectionStrings__Database` and `Database__ServerCa` already have one, written by Terraform).
   A provider you have not set up yet still needs a version, and Secret Manager refuses an empty one: add a single
   space, which the processes treat as unset (`printf ' ' | gcloud secrets versions add Ticketmaster__ApiKey ...`).
   `Spotify__TokenKey` must be the same value for the API and the worker (it is: one secret, two readers).
   Beta and prod use different Spotify apps and Apple keys, so a beta tester never touches prod credentials.
5. **Overlay.** In `k8s/overlays/<env>/`, set the project id and region (`kustomization.yaml`,
   `serviceaccounts.yaml`, `secrets.yaml`), the hostname (`gateway.yaml`) and the same URL as `App__PublicOrigin`,
   `ForwardedHeaders__KnownProxies` to output `api_ip_address`, and the non-secret provider settings (`Auth__*__ClientIds`, `Spotify__ClientId`,
   `Spotify__RedirectUris`, `Cors__AllowedOrigins`), and the web apps' hostnames (`gateway.yaml`) and settings
   (`web-config`). Commit these; they hold no secrets.
6. **GitHub.** Create the environment (`beta` or `prod`) under repository settings with the
   variables `GCP_PROJECT_ID`, `GCP_REGION`, `GCP_WIF_PROVIDER` (output `workload_identity_provider`), and
   `GCP_DEPLOY_SA` (output `deploy_service_account`). Add required reviewers on `prod`. The Workload Identity
   provider only accepts tokens from this repository running in the GitHub environment with the same name, so
   a prod deploy cannot skip the approval. Finally set the repository variable `DEPLOY_ENABLED` to `true`: the
   Deploy workflow is skipped without it, so pushes stay green before the environments exist.
7. **First deploy.** Push to `main`, or run the Deploy workflow by hand (`beta`). Or from a machine with the tools
   (the first deploy of an empty database needs no backup, and the health check needs DNS and the certificate):
   ```bash
   gcloud container clusters get-credentials fandomscore-beta --region us-central1 --project project-ef36a8a9-d958-4163-b4c
   gcloud auth configure-docker us-central1-docker.pkg.dev
   for t in api worker migrate web promoter-web; do
     docker build --target $t -t us-central1-docker.pkg.dev/project-ef36a8a9-d958-4163-b4c/fandomscore/$t:$(git rev-parse HEAD) . && docker push "$_"
   done
   SKIP_BACKUP=1 SKIP_HEALTH_CHECK=1 infrastructure/k8s/deploy.sh beta $(git rev-parse HEAD)
   ```
   Then `curl https://api.beta.example.com/v1/health`.

## Web apps

The fan app (`apps/mobile`, its web build) and the promoter dashboard (`apps/promoter-web`) are static sites. Each
is an image (`Dockerfile` targets `web` and `promoter-web`) holding the built files and an unprivileged nginx
(`infrastructure/web/`) that serves them, sends every unknown path to `index.html` so deep links reload, caches
the hashed assets for a year, and adds security headers. They run in the same cluster behind the same Gateway and
address as the API, each on its own hostname (beta: `app.beta.firstline-5730kl.com` and `promoters.beta.firstline-5730kl.com`),
with its own certificate in the same certificate map (Terraform `web_domains`) and the same Cloud Armor policy.

- **One image for every environment.** The deploy promotes beta's image digests to prod, so nothing
  environment-specific is built in. At startup nginx writes `/config.js` from its environment (`API_URL`,
  `DEV_SIGN_IN`, `GOOGLE_WEB_CLIENT_ID`, `SPOTIFY_CLIENT_ID`, from the overlay's `web-config`), and every page
  loads it before the app. These are public values; nothing secret belongs there.
- **The API must allow both origins**: `Cors__AllowedOrigins` in the overlay's `app-config`.
- **Google sign-in**: in the Google Cloud console, the web OAuth client (the one in `web-config`) needs both
  hostnames as authorized JavaScript origins (`https://app.beta.firstline-5730kl.com`,
  `https://promoters.beta.firstline-5730kl.com`) and `https://<host>/google-callback` for each as an authorized
  redirect URI.
- **Spotify** (when `Spotify__ClientId` is set): the Spotify app and the API's `Spotify__RedirectUris` need the
  fan app's callback, `https://app.beta.firstline-5730kl.com/spotify-callback`.
- **Check**: `curl https://app.beta.firstline-5730kl.com/config.js` shows the settings; `deploy.sh` checks both hostnames
  after a rollout and warns (does not fail) if one doesn't answer yet, since a new hostname waits for DNS and its
  certificate.

## Deploying

`deploy.sh <beta|prod> <tag>` sets the three image tags in the overlay, takes the Cloud SQL backup, applies
everything except the Deployments, deletes the previous `migrate` Job and creates a new one, waits for it to
complete (prints its log, fails the deploy if it fails), applies the Deployments, waits for the api, worker, and
redaction rollouts, and
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
  stop the API and workers first (`kubectl scale deployment/api deployment/worker deployment/redaction --replicas=0 -n fandomscore`),
  then `gcloud sql backups list --instance fandomscore-beta` and `gcloud sql backups restore <id> --restore-instance
  fandomscore-beta`, redeploy the previous commit, and scale back up. Writes made after the backup are lost;
  for a narrower window, clone to a point in time instead (Backups and restore). On prod, prefer the clone.
- **Config change without a code change.** Edit the overlay and run `deploy.sh` with the current SHA. The
  ConfigMap name carries a content hash, so pods restart with the new values.
- **Secret rotation.** Add a new secret version; restart the Deployment (`kubectl rollout restart deployment/api
  -n fandomscore`) because the mount is read at start. For `Spotify__TokenKey`, follow `.env.example`: add
  `Spotify__PreviousTokenKeys` as a secret (`gcloud secrets create`), grant the api and worker identities access,
  add it to both `SecretProviderClass` lists, deploy, and remove it after every user has synced.

### Turning an environment off and on

Beta costs money while it idles (Autopilot pods, the load balancer, Cloud SQL). Its desired state lives in git, in
`infrastructure/k8s/overlays/beta/state` (`on` or `off`), and the Deploy workflow reconciles it on every push:

```bash
infrastructure/k8s/set-state.sh beta off   # commits "beta off" and pushes
infrastructure/k8s/set-state.sh beta on    # commits "beta on" and pushes
```

`off` runs `infrastructure/k8s/shutdown.sh`: every Deployment goes to zero replicas and the Gateway is deleted, which
removes the load balancer. Data, config, secrets, certificates, DNS, and Cloud SQL stay (stopping Cloud SQL is a
manual `gcloud sql instances patch --activation-policy NEVER`, not done here). Pushes while the state is `off` still
run CI but skip the image build and rollout. `on` is an ordinary deploy; a recreated Gateway needs about 5 minutes
before HTTPS answers and about 15 before the web apps' backends are healthy, which `deploy.sh` waits for.

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

`RUNBOOK.md` is the page to open when something is wrong. What exists:

- Dashboard (Terraform output `dashboard_url`, `dashboard.tf`): requests per second by response class and latency
  percentiles at the load balancer, running containers and CPU/memory per workload, the worker's failed and
  given-up jobs, container restarts, health-check failures by region, Cloud SQL CPU/memory/disk and connections,
  and a panel of recent errors.
- Alerts (`monitoring.tf`), all emailed to `alert_email`: API down (uptime check failing from more than one region
  for five minutes), API returning errors (more than 5 % 5xx over five minutes), pods restarting, worker gave up on
  a job, worker cannot reach the queue, Cloud SQL under pressure, and the budget at 50 %, 90 %, 100 % and
  forecast.
- Worker liveness: the worker serves no traffic, so each job loop reports to a heartbeat and `GET /healthz` on port
  8081 returns 503 once a loop has been silent for ten minutes; the pod then restarts (`workers/README.md`).
  Job failures are log lines (`failed on attempt`, `failed permanently`) turned into log-based metrics.
- Logs: Cloud Logging, `resource.type="k8s_container" resource.labels.namespace_name="fandomscore"`. The API and
  worker log JSON, so fields are queryable. Locally: `kubectl logs deployment/api -n fandomscore`.
- Health: `https://<api_domain>/v1/health`, checked every minute from several regions.
- Images: Artifact Registry scans each pushed image; findings are on the image in the console.
- Database: Query Insights is on for the Cloud SQL instance.

## Known limitations

- Nothing here has been applied to a real project yet: Terraform passes `validate` and the manifests build (CI
  checks both, and builds the images, on every push), but the first `apply` and deploy will surface anything the
  provider rejects at runtime. The dashboard and alert definitions in particular name metric types and log
  fields that `validate` cannot check; confirm each chart has data and each alert can fire on beta.
- The worker's liveness probe only catches a loop that stops iterating. A loop that keeps erroring (database
  unreachable) stays "alive" on purpose; the "worker cannot reach the queue" alert covers that.
- Stored files are in Cloud Storage (`Storage__Provider=gcs`); the API and the redaction Deployment must name the
  same buckets, so `Storage__*` settings live in the shared `app-config` ConfigMap, not one Deployment.
- The kinds the redaction Deployment runs are listed twice, `Jobs__OnlyKinds` in `redaction-deployment.yaml` and
  `Jobs__ExceptKinds` in `worker-deployment.yaml`; change both. A kind in neither runs in both (harmless: claims
  never overlap), a kind in both runs nowhere and queues up. A new job that touches stored files belongs in both
  lists, or it fails with a permission error in the worker.
- The `SecretProviderClass` lists in each overlay duplicate the `secret_readers` map in Terraform; change both.
- The migration safety check is a pattern match, not a parser: it knows the common breaking statements and
  ignores function bodies. Slow-but-additive migrations (indexes on big tables) are on the author.
- No promoter dashboard deployment yet (`apps/promoter-web` is not built).
