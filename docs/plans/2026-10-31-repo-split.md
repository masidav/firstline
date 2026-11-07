# Plan: split the monorepo so each piece ships on its own

Status: steps 1–3 done (2026-11-03, 2026-11-07): the pieces deploy, version, and test separately inside this one
repository. **Decided 2026-11-07 (owner): stop here for now.** Per-component deploys already exist, so steps 4–6
(the actual extraction) are parked; the open questions at the end of this file stay open until that changes.
Date: 2026-10-31.

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
   **Done 2026-11-03** (56707f5). Differs from the sketch above in one way: `versions.env` (per overlay) says
   `auto` or a pinned commit per component, and `auto` resolves to the last commit that changed the paths the
   component's images are built from (`infrastructure/k8s/versions.sh`, the one component → images → paths map).
   CI doesn't commit version bumps back to `main` (that would race the agents pushing there, and pushes made with
   the workflow token don't trigger workflows); the version follows from git history, so every run computes the
   whole set as of its commit, and a newer push replacing a queued run can't leave a component behind. A run whose
   commit is no longer `main`'s head skips. Images are tagged with their component's version; CI builds only tags
   the registry lacks. `deploy.sh <env>` compares with what's running: new backend version → backup, migrate Job,
   settings, seed, rollout; web or manifest change only → rollout without the Job; nothing differs (`kubectl diff`,
   `settings`/`seed` recorded in ConfigMap `deploy-inputs`) → nothing. A manual run (`deploy: all`) or
   `DEPLOY_ALL=1` does every step. Rollback of one component = pin its line (`infrastructure/README.md`,
   "Deploying"). Verified on beta: Deploy run 41708163437 (first run, at 4e96c90) re-pinned everything (backend
   288e4d6 with backup and migrate Job, web/promoter-web 6a43873, all images already in the registry, nothing
   rebuilt); run 41709111106 (64c7bb2, a comment in `apps/mobile/src/config.ts`) built only `web:64c7bb2`, showed
   only the web image in the diff, took no backup and left the migrate Job (20:01:54Z), and replaced only the web
   pods; api, worker, redaction, and promoter-web kept their ReplicaSets and pods from 20:02Z. A docs-only push
   (this note) deploys nothing.
2. **Contract releases + vendored contract.** Add the release tagging and `oasdiff` check; switch both apps to
   `contract/openapi.yaml` + `sync:api`. Ends the `../../openapi.yaml` reach-around.
   **Done 2026-11-07.** The version is `info.version` in `openapi.yaml` (set to 1.0.0; it was a placeholder 0.1.0),
   bumped by whoever edits the file, rather than a counter CI commits back (CI doesn't push to `main`, step 1).
   CI job `contract`: compares with the newest `contract-v*` tag's copy; a changed file needs a higher version
   (`tools/ci/check_contract_version.py`, unit-tested); `oasdiff breaking --fail-on ERR` between the two makes a
   major bump mandatory. Two oasdiff checks are lowered to info in `tools/ci/oasdiff-severity.txt` (adding a
   response enum value or a oneOf variant), because adding an error `code` is this project's most common
   contract change and the apps handle unknown codes generically; the file takes no comments. Job
   `contract-release` (pushes to `main` only, `contents: write`) runs `gh release create contract-v<version>
   openapi.yaml` with the oasdiff changelog as notes. Both apps: `contract/openapi.yaml` + `contract/VERSION`
   (+ a README), `npm run generate:api` reads the vendored copy, and `npm run sync:api` (`scripts/sync-api.mjs`)
   refreshes it from `../../openapi.yaml` while the app is here, or from the release with
   `npm run sync:api -- 1.3.0` (`gh release download`, so a private repository works without a registry token).
   The apps' CI jobs run `sync:api` and fail on a diff in `contract/` or `schema.d.ts`. Known gap: the concurrency
   group cancels a superseded CI run, so a version that lives on `main` for one commit only may never get its
   release; the next run releases the version then current. Verified: `python3 -m unittest discover -s tools/ci`
   (contract tests included) OK; the check script on the real file: bumped → pass, identical → `changed=false`,
   edited without a bump → exit 1 with the message, breaking with a major bump → pass; oasdiff on `openapi.yaml`
   at HEAD~30 vs now with the severity file exits 0 (about 1,400 enum-value additions and 7 oneOf additions
   lowered, nothing else); `sync:api` in both apps leaves `schema.d.ts` byte-identical; actionlint clean.
3. **Testkit image.** Publish it; switch `apps/*/e2e/run.sh` and the CI `e2e` job to use it. Move
   `infrastructure/web` into each app (or a `web-base` image built by infra).
   **Done 2026-11-07.** `Dockerfile` target `testkit` (runtime image, not chiseled: python3 + psql, the migrator's
   publish output, `tools/dev/seed.py` and `spotify_connect.py`, `tools/testkit/entrypoint.sh`; runs as `app`):
   waits for PostgreSQL, migrates, seeds (`SEED_ARGS`, default `--fake`), exits. `tools/testkit/compose.yml`:
   `db` (postgres:16, port `E2E_DB_PORT` for the fixtures' direct SQL), `testkit` (once), `api` (Development, dev
   sign-in, 1000 sign-ins a minute, CORS for both apps' origins, uploads on a tmpfs; `E2E_SIGNING_KEY` required so
   no key sits in the repo). Images default to local builds (`build:` from the repo root) and switch to published
   ones with `E2E_IMAGE_PREFIX`/`E2E_BACKEND_TAG`; `versions.sh` lists `testkit` among the backend's images, so
   the Deploy workflow pushes it with `api`/`worker`/`migrate` at every backend version (nothing deploys it).
   Both `e2e/run.sh` scripts start the stack and need neither the .NET SDK nor Python; both Playwright configs lost
   their `dotnet run` server. The CI `e2e` job builds `api` + `testkit` (gha cache), starts the stack, runs both
   suites against it, and prints the backend logs on failure. `infrastructure/web` moved to `apps/mobile/web` and
   a copy to `apps/promoter-web/web` (identical today; each app's image copies its own), so each app's image
   builds from its own directory alone; `versions.sh` paths updated (also adding `packages/music-providers`, which
   the Dockerfile copied but the backend's path list missed). Verified: compose stack from this checkout → testkit
   log "migrations" then "seed", `GET /v1/health` ok, `/v1/me` 401, 201 fans / 8 artists / 204 registrations in
   the database; fan app e2e 11/11 and promoter e2e 4/4 (axe scan included) against it; `versions.sh beta`
   resolves all three components; shellcheck, hadolint, actionlint, gitleaks clean; both apps typecheck,
   Jest 441/441, Vitest 29/29; `web` and `promoter-web` images build and the web one serves `/healthz`,
   `/config.js`, and the security headers; `dotnet build` 0 warnings, `dotnet test` 475/475.
4. **Extract `firstline-infra`** with `git filter-repo --path infrastructure/ --path .github/workflows/deploy*.yml`
   (keeps history). Apply the Terraform WIF change first: the attribute condition lists all four repos
   (image pushes) and only the infra repo gets deploy roles. Move GitHub environment secrets/vars. Do one manual
   beta deploy from the new repo before switching on automatic deploys and switching them off here.
5. **Extract `firstline-promoter-web`** (smallest, least churn: a good rehearsal), then **`firstline-fan-app`**
   (move EAS/Apple secrets and the `mobile-*` workflows; check `eas.json`/`app.config.ts` for root-relative paths).
6. **Clean up the backend repo:** remove the moved dirs, slim the Dockerfile to `api`/`worker`/`migrate`/`testkit`,
   update `CLAUDE.md`, `README.md`, `docs/DEVELOPMENT.md`, `TODO_INDEX.md`, and copy the agent hooks
   (`tools/claude-config`, `guard_paths.py`) to each new repo with only the rules that apply there (the migration
   guard stays here; the `.env` guard goes everywhere). Record the decision in `docs/adr/0008-repo-split.md` (0007
   is music providers).

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
