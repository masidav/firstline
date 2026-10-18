# TODO — Project documentation

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect the existing README, schema, code, migrations, and deployment notes; record what already exists. See `REPOSITORY_INVENTORY.md`.
- [ ] Document product scope, feature boundaries, and the tracked-artist-only rule.
- [ ] Document the current architecture and data flow from inputs through storage to scoring.
- [ ] Document API endpoints, request/response shapes, authentication requirements, and error conventions.
- [ ] Document database tables, relationships, triggers, functions, and integrity constraints from the actual schema.
- [ ] Record each proposed or open decision separately from confirmed requirements.
- [ ] Document local setup, environment variables, test commands, migrations, and seed procedures.
- [ ] Add an architecture decision record for each major technology or product decision once confirmed.
- [ ] Verify all documentation links and paths after the repository structure is finalized.

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
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
