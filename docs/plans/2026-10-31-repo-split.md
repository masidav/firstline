# Plan: split the monorepo so each piece ships on its own

Status: proposal, nothing done yet. Date: 2026-10-31.

## Why, and what has to change

Today one commit builds all five images (`api`, `worker`, `migrate`, `web`, `promoter-web`) with one tag, and
`infrastructure/k8s/deploy.sh` rolls all of them out together, every time, including a Cloud SQL backup and the
migrate Job when only a web app changed. The goal is that a fan-app change, a dashboard change, a backend change,
or an infrastructure change can each be built, tested, released, and rolled back without the others.

What couples the pieces today (from the code, not guesses):

| Coupling | Where | What it means for a split |
|---|---|---|
| API contract | `openapi.yaml` at the root; both apps run `openapi-typescript ../../openapi.yaml`; `ContractCheckedFactory` reads it | Needs a versioned, published contract |
| Lockstep commits | In the last 60 days, ~50 of 172 code commits touched backend + `openapi.yaml` + one or both apps | These become 2–3 ordered changes; backward compatibility is now required, not optional |
| Shared schema | API, worker, migrator, and tests all use `database/` and `packages/*` (`ProjectReference`) | Keep them in one repo |
| E2E tests | `apps/*/e2e` run `dotnet run --project database`, `tools/dev/seed.py`, and the API from source, and read the DB directly (`E2E_DATABASE_URL`) | The backend must publish a runnable test stack (images) |
| One deploy tag | `deploy.sh` sets all five images to `${tag}` | Pin a version per component |
| Web serving config | `infrastructure/web/*` (nginx, `/config.js`) is copied into both web images | Moves with the apps (or into a small shared base image) |
| CI identity | Terraform WIF: `attribute_condition = assertion.repository == "masidav/firstline"` (`modules/environment/cicd.tf`) | Must allow the new repos to push images |

## Target repos

The repo count is set by how often each piece is released on its own, not by the directory tree. Only split
where the pieces actually ship separately.

| Repo | Contents (from today's tree) | Publishes |
|---|---|---|
| **`firstline`** (this repo, kept: history, issues, secrets, live deploy) → becomes the **backend** | `services/api`, `workers`, `database` (migrations + invariants), `packages/*`, `tests/`, `openapi.yaml`, `tools/dev/seed.py`, `tools/ci`, `tools/load`, product docs (`executive_summary.txt`, `PROJECT_TODO.md`, `docs/adr`, `docs/plans`) | `api`, `worker`, `migrate`, `testkit` images; contract releases `contract-vX.Y.Z` |
| **`firstline-fan-app`** | `apps/mobile` (incl. e2e, maestro, EAS config), `mobile-ios.yml`, `mobile-release.yml`, nginx web config | `web` image; App Store / EAS builds |
| **`firstline-promoter-web`** | `apps/promoter-web`, its copy of the nginx config | `promoter-web` image |
| **`firstline-infra`** | `infrastructure/terraform`, `infrastructure/k8s`, `RUNBOOK.md`, `deploy*.yml`, `db-*.sh`, `set-state.sh`, `shutdown.sh` | Deploys: the source of truth for *what version runs where* |

Kept together on purpose:
- **API + worker + migrations + packages.** They share one schema and one test suite (`tests/` references the
  API, worker, and database projects), and the migration-safety check (`tools/ci/check_migration_safety.py`)
  exists so that old and new code can share the database during a rollout. Splitting them would mean versioned
  NuGet packages for `packages/spotify` etc. with no independent release to show for it. Revisit only if the
  worker ever gets its own owner or its own release cadence.
- **Product docs stay in the backend repo**, since most planning is about the backend. The app repos link to them.

Left for later: `crawlers/` (standalone Python, rarely changed) can stay put or move to `firstline-crawlers`
whenever convenient; nothing depends on it.

## The contract between repos

1. **The backend owns `openapi.yaml`.** On every push to main that changes it, CI tags a contract release
   (`contract-v1.N.0`, semver: minor = additive, major = breaking) and attaches `openapi.yaml` to a GitHub release.
2. **Clients vendor a pinned copy.** Each app has `contract/openapi.yaml` + `contract/VERSION`;
   `npm run sync:api -- 1.42.0` downloads that release and regenerates `src/api/schema.d.ts`. CI checks that the
   vendored file matches its tag and that the generated types are current (today's "API types" CI step, unchanged).
   Vendoring is simpler than an npm package on GitHub Packages (no registry token in every CI job and dev machine).
3. **Breaking changes become a CI failure in the backend.** Add an `oasdiff breaking` check against the last
   contract release. A field or endpoint is removed only after both apps' deployed versions no longer use it
   (expand → migrate clients → contract). This is needed anyway once the iOS app ships, because old App Store
   builds keep calling the API for months.
4. **Images carry their contract version** (`LABEL firstline.contract=1.42.0`). The infra deploy refuses to roll
   out a client whose contract is newer than the backend version running in that environment.

The common full-stack feature turns into: (a) backend PR adds endpoint/field, ships, contract 1.43.0 released;
(b) app PR runs `sync:api -- 1.43.0`, uses it, ships. Same work, and the deploy order is now explicit.

## Testing across repos

- **Backend testkit image.** It bundles the migrator and `seed.py --fake`, plus a `docker-compose.e2e.yml`
  (Postgres 16 + migrate + seed + api at a given tag). App e2e (`apps/*/e2e/run.sh`, the CI `e2e` job) starts that
  stack with the backend version matching `contract/VERSION` instead of building the backend from source.
  The fixtures that read the DB directly keep working; they get the same `E2E_DATABASE_URL`.
- **Two runs per app:** on each push, against the pinned backend version (must pass); nightly, against the backend's
  latest `main` image, to catch drift before the next contract bump (failure opens an issue, doesn't block).
- **Backend CI** keeps the xUnit/contract/invariant/seed jobs as today. It loses the browser e2e job, which moves
  to the app repos as above.
- **Infra CI** keeps `terraform validate`/kustomize builds; `tools/ci/smoke-images.sh` moves to the backend.

## Deploying pieces separately

`firstline-infra` holds a pinned version per component per environment, e.g.
`infrastructure/k8s/overlays/beta/versions.env`:

```
api=4867be8        # api, worker, migrate share the backend commit
web=a1b2c3d
promoter-web=9f8e7d6
```

- **Component repo, on push to main:** build, test, push its image(s) to the beta Artifact Registry, then
  commit a version bump to `firstline-infra` (via a GitHub App token; or open a PR there once PRs are adopted).
- **Infra repo, on change to `versions.env`:** `deploy.sh` deploys only the components whose version changed.
  The Cloud SQL backup and migrate Job run only when the backend version changes; a web-only deploy is a plain
  rollout of one Deployment. Rollback = revert one line.
- **Prod promotion:** copy beta's pinned versions into prod's file (and the images from beta's registry, as
  `deploy-environment.yml` does today with `promote_from`).
- `DEPLOY_ENABLED` / `PROD_DEPLOY_ENABLED` and the on/off state (`set-state.sh`) move to the infra repo unchanged.

## Migration steps

Each step leaves beta deployable. Steps 1–3 happen inside the monorepo before anything moves, so the risky parts
are tested where rollback is trivial.

1. **Per-component deploy, still in one repo.** Add `versions.env`, teach `deploy.sh` to deploy only changed
   components (and skip backup/migrate when the backend didn't change). Verify with a web-only deploy to beta.
2. **Contract releases + vendored contract.** Add the release tagging and `oasdiff` check; switch both apps to
   `contract/openapi.yaml` + `sync:api`. Ends the `../../openapi.yaml` reach-around.
3. **Testkit image.** Publish it; switch `apps/*/e2e/run.sh` and the CI `e2e` job to use it. Move
   `infrastructure/web` into each app (or a `web-base` image built by infra).
4. **Extract `firstline-infra`** with `git filter-repo --path infrastructure/ --path .github/workflows/deploy*.yml`
   (keeps history). Apply the Terraform WIF change first: the attribute condition lists all four repos
   (image pushes) and only the infra repo gets deploy roles. Move GitHub environment secrets/vars. Do one manual
   beta deploy from the new repo before switching on automatic deploys and switching them off here.
5. **Extract `firstline-promoter-web`** (smallest, least churn: a good rehearsal), then **`firstline-fan-app`**
   (move EAS/Apple secrets and the `mobile-*` workflows; check `eas.json`/`app.config.ts` for root-relative paths).
6. **Clean up the backend repo:** remove the moved dirs, slim the Dockerfile to `api`/`worker`/`migrate`/`testkit`,
   update `CLAUDE.md`, `README.md`, `docs/DEVELOPMENT.md`, `TODO_INDEX.md`, and copy the agent hooks
   (`tools/claude-config`, `guard_paths.py`) to each new repo with only the rules that apply there (the migration
   guard stays here; the `.env` guard goes everywhere). Record the decision in `docs/adr/0007-repo-split.md`.

Local dev after the split: a `firstline-dev` checkout script (or a short section in each README) clones the repos
side by side; `docker compose` with the testkit image runs the backend for app work without the .NET SDK.

## Costs to accept

- A full-stack feature becomes 2–3 commits in different repos, in order, instead of one. Agents working in one
  repo can no longer change the API and the app together; the contract version is the handoff.
- Contract discipline (additive changes, deprecation before removal) becomes mandatory.
- More CI plumbing: a cross-repo token, four sets of workflows, nightly drift runs.

If the main goal is only *deploying* pieces separately, steps 1–3 deliver most of it without splitting at all.
Step 1 alone removes the "every change redeploys everything" problem. That is a fine place to stop and reassess.

## Decisions for the owner (not decided here)

- Personal account or a GitHub organization for the repos (an org makes the GitHub App and shared secrets easier).
- Whether to start using PRs for the version bumps in `firstline-infra` (today everything commits straight to main).
- Repo names.
- Stop after step 3 (independent deploys, one repo) or go through with the full split.
