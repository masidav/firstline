# TODO — Project documentation

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect the existing README, schema, code, migrations, and deployment notes; record what already exists. See `REPOSITORY_INVENTORY.md`.
- [ ] Document product scope, feature boundaries, and the tracked-artist-only rule. *2026-11-03: scope is in
      `executive_summary.txt` and PROJECT_TODO "Product direction"; the tracked-artist rule in `CLAUDE.md` and
      `ARCHITECTURE.md`. Open: the root `README.md` still describes the TODO pack, not the product.*
- [ ] Document the current architecture and data flow from inputs through storage to scoring. *2026-11-03:
      `ARCHITECTURE.md` still lists intended boundaries (it mentions Redis, which isn't used) rather than the built
      system; ADRs 0001–0006 and feature READMEs cover pieces.*
- [x] Document API endpoints, request/response shapes, authentication requirements, and error conventions.
      *`openapi.yaml` (operations, schemas, `security`, problem codes), checked against the API by the contract and
      route tests; conventions in `CLAUDE.md`. 416/416 on 2026-11-03.*
- [ ] Document database tables, relationships, triggers, functions, and integrity constraints from the actual schema.
      *2026-11-03: the migrations' comments and `services/api/src/features/privacy/DATA_INVENTORY.md` (every table)
      cover much of it; `database/README.md` is still a template.*
- [x] Record each proposed or open decision separately from confirmed requirements. *PROJECT_TODO "Open
      decisions", with provisional values marked "set provisionally, confirm or change". Checked 2026-11-03.*
- [x] Document local setup, environment variables, test commands, migrations, and seed procedures.
      *`docs/DEVELOPMENT.md` (First run, Seed data, Tests, Migrations, Configuration) and `.env.example`. Checked
      2026-11-03.*
- [ ] Add an architecture decision record for each major technology or product decision once confirmed.
      *2026-11-03: technology ADRs 0001–0006 exist; confirmed product decisions are recorded in PROJECT_TODO, not
      as ADRs.*
- [x] Verify all documentation links and paths after the repository structure is finalized. *2026-11-03: every
      relative link in the 175 tracked Markdown files resolves (script check, 0 broken). Not checked in CI.*

## Developer tooling (Phase 14)
- [x] Initialize git and add a private remote. (Protecting `main` deferred.)
- [x] Add CI for build and tests on pull requests.
- [x] Write `CLAUDE.md` with commands and conventions.
- [x] Add `.claude/agents/` definitions and `.claude/settings.json` hooks; document what each one does.
- [ ] Set up Claude Code remote access (Remote Control or Claude Code on the web) and GitHub Actions.
- [ ] *Deferred.* Install OpenClaw on an isolated host with a repo-scoped token; review every skill before enabling it.
- [x] Document agent access, audit, and revocation steps. (`agent-tooling.md`)

## Verification
- [ ] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. *2026-11-03: link check above; `dotnet test` 416/416.*
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
