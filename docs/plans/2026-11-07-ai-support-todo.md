# To do: AI support for speed and quality

Date: 2026-11-07. How to get more out of Claude Code (and agents in CI) on this project without losing the quality
bar: hooks, skills, agents, MCP servers, scheduled runs, and the goals that tell us whether it works. Builds on
`docs/agent-tooling.md` (principles, what is installed, remote access) and Phase 14 in `PROJECT_TODO.md`.
Mark `[x]` only when installed and verified; record what was run, as everywhere else in this repo.

Priorities: **P1** do next (cheap, prevents a known failure or removes a daily chore), **P2** soon, **P3** when the
project grows (real users, more than one person, pull requests). Effort: S = under an hour, M = half a day, L = more.

## Already in place (keep, don't duplicate)

| What | Where | Note |
| --- | --- | --- |
| Project instructions | `CLAUDE.md` | Loaded every session: commands, layout, rules, workflow. |
| Guard hook | `tools/agent-hooks/guard_paths.py` (PreToolUse) | Blocks edits to applied migrations and any `.env` access. Unit-tested in CI. |
| Build hook | `tools/agent-hooks/build_after_edit.py` (PostToolUse) | `dotnet build` after a .NET edit; errors go back to Claude. |
| Agents | `.claude/agents/migration-reviewer.md`, `api-contract-checker.md` | Run on request. Reviewed source in `tools/claude-config/`. |
| Memory | `~/.claude/projects/-home-glide-firstline/memory/` | Per machine: shared-checkout hazards, hosting status, decisions. |
| CI as backstop | `.github/workflows/ci.yml` | Migrations, contract version, secrets, lint, images, e2e, hook tests. |
| Weekly product docs | `.github/workflows/weekly-docs.yml` | Built, but failing every Monday: no `ANTHROPIC_API_KEY` (`docs/OWNER_TODO.md` 11). |
| Worktrees for parallel sessions | `.claude/worktrees/` | Used by hand; the shared checkout still bites (memory `parallel-sessions-worktrees`). |

## A. Hooks: catch it before CI does

| Item | Description | Setup | Benefit | Priority / effort | Notes |
| --- | --- | --- | --- | --- | --- |
| [ ] A1. Block bulk staging in the shared checkout | Refuse `git add -A`, `git add .`, `git commit -a`, and bare `git stash` when the working tree has changes outside the files this session edited. | Extend `guard_paths.py` Bash rules; track edited paths in the hook's state file; unit tests like `test_guard_paths.py`. | Prevents the 2026-10-24 incident (commit a3b3964 reverted merged work) from recurring with any agent. | P1 / S | Keep "allow when unsure": the hook is a guard rail, not a sandbox. |
| [ ] A2. Session start: stale-checkout warning | On `SessionStart`, run `git fetch` and print how far HEAD is behind `origin/main`, the dirty file count, and whether another worktree session is active. | New hook script `tools/agent-hooks/session_start.py`; `SessionStart` entry in `settings.json`. | Every session begins knowing whether to rebase or to work in a worktree, instead of learning it at push time. | P1 / S | Pairs with A1 and D3. |
| [ ] A3. Contract edit follow-through | After an edit to `openapi.yaml`: run `check_contract_version.py` against the latest `contract-v*` tag and `npm run sync:api` in both apps; report the result. | PostToolUse hook matching that path; `git tag` lookup; call the two npm scripts. | The version bump and the vendored copies are never forgotten; CI's contract job stops being the first place it fails. | P1 / S | Needs `node_modules` installed; skip quietly when missing. |
| [ ] A4. TypeScript check after app edits | `tsc --noEmit` for the app whose `.ts`/`.tsx` file changed, like `build_after_edit.py` does for .NET. | Extend `build_after_edit.py` (or a sibling) with the two app roots; debounce by file. | Type errors surface in seconds, not after a full `npm test` or in CI. | P2 / S | `tsc` on the fan app takes ~20 s; acceptable, or use `--incremental`. |
| [ ] A5. New-migration check | When a new `database/migrations/*.sql` is written: run `tools/ci/check_migration_safety.py` on it and remind about an `invariants.sql` entry for new triggers or constraints. | PostToolUse hook on that path; reuse the CI script. | Rollout-safety findings appear while the migration is still being written. | P2 / S | Complements the existing block on *editing* applied migrations. |
| [ ] A6. No mutating prod commands | Refuse `deploy.sh prod`, `kubectl`/`gcloud` with the prod project or `-n prod`, `set-state.sh prod`, `db-wipe.sh prod` from an agent session. | Bash matcher rules in `guard_paths.py`, with tests. | Enforces "agents never deploy to production" (`agent-tooling.md`) mechanically, not by convention. | P2 / S | Beta stays allowed; the wipe window and reset rules already cover beta. |
| [ ] A7. Stop hook: finish-line checklist | When Claude ends a turn with uncommitted changes, print: files changed, whether `dotnet test`/`npm test` ran this session, whether a TODO entry was updated, whether `openapi.yaml` changed without a version bump. | `Stop` hook reading the session's tool log (the hook payload includes the transcript path). | The "verified and recorded" rule in `CLAUDE.md` gets a reminder exactly when it matters. | P2 / M | Advisory only (exit 0 with a message); never block the stop. |

## B. Skills and agents: make the recurring rituals one command

| Item | Description | Setup | Benefit | Priority / effort | Notes |
| --- | --- | --- | --- | --- | --- |
| [ ] B1. `/verify` skill | The full local verification in one step: `dotnet build` + `dotnet test`, `npm run sync:api`/`typecheck`/`test` in both apps, shellcheck, hadolint, actionlint, gitleaks (the docker one-liners from `ci.yml`), then a results table in the words CLAUDE.md asks for. | `.claude/skills/verify/SKILL.md` + `tools/dev/verify.sh`; reviewed source under `tools/claude-config/skills/`. | Same checks every time, reported with real numbers; removes the ten-command dance done by hand today (2026-11-07 repo-split commit). | P1 / M | Option flags: `--fast` (no e2e), `--e2e` (adds `apps/*/e2e/run.sh`). |
| [ ] B2. `/ship` skill | Commit and push the way this repo needs: check `git status` for foreign files, stage only own paths, rebase on `origin/main`, push (via a temp worktree if the checkout is dirty), then `gh run watch` and report CI and Deploy. | Skill file encoding the recipe from memory `parallel-sessions-worktrees` and `CLAUDE.md` Workflow. | The shared-checkout rules stop living only in one machine's memory; every session ships the same safe way. | P1 / S | Calls `/verify` first unless told it already ran. |
| [ ] B3. `/e2e` skill | Start `tools/testkit/compose.yml`, run one or both suites, keep the stack up for `--headed` debugging, tear down. | Wraps `apps/*/e2e/run.sh`; documents `E2E_BACKEND_TAG` for testing against a published backend. | Browser verification becomes cheap enough to run for every UI change. | P2 / S | Needs Chrome or `npx playwright install chromium` once. |
| [ ] B4. `/feature` skill (plan before code) | Scaffold a feature: `src/features/<name>/TODO.md` from the house template, list the `PROJECT_TODO.md` open decisions it touches, point at `openapi.yaml` conventions, the migration rules, and the job queue. | Skill with a template and a short checklist; asks which phase it belongs to. | Consistent plans and TODOs; fewer "decided silently in code" slips. | P2 / S | The template already exists implicitly in the feature TODOs; write it down once. |
| [ ] B5. `/preflight` fan-out | Before a large change ships, run the existing `migration-reviewer` and `api-contract-checker` agents plus `/code-review` in parallel on the diff and merge the findings. | Skill that spawns the agents (or a small Workflow); the `code-review` skill is built in. | Independent reads of migrations, contract, and correctness without anchoring on the author's view. | P2 / S | Use `/code-review ultra` only for infra or auth changes (billed, many agents). |
| [ ] B6. New agents | `security-reviewer` (auth, `/admin` policy, CORS, RFC 9457 codes, secrets in logs), `test-gap-finder` (operations in `openapi.yaml` with no test in `tests/`), `docs-linker` (relative links resolve; today's manual check, `docs/TODO.md`). | Agent `.md` files in `tools/claude-config/agents/`, installed into `.claude/agents/`; read-only tools. | Reviews that a person would skip on a busy day run anyway. | P2 / M | Keep agents read-only; they report, the main session fixes. |
| [ ] B7. Per-directory `CLAUDE.md` | Move app-, worker-, and infrastructure-specific rules into `apps/mobile/CLAUDE.md`, `apps/promoter-web/CLAUDE.md`, `workers/CLAUDE.md`, `infrastructure/CLAUDE.md`; keep the root one short. | Claude Code loads nested `CLAUDE.md` files when working in those directories. | Less context per session, rules closer to the code they govern, fewer wrong-layer mistakes. | P2 / M | Root keeps the rules that apply everywhere (migrations, ledger, secrets, snake_case). |

## C. MCP servers and connectors: give Claude the right eyes

| Item | Description | Setup | Benefit | Priority / effort | Notes |
| --- | --- | --- | --- | --- | --- |
| [ ] C1. PostgreSQL MCP (local and e2e only) | A read-only connection to the compose development database (and the e2e stack's), so Claude can inspect schema, triggers, and seed data while debugging. | `.mcp.json` with a Postgres MCP server pointing at `127.0.0.1` with a read-only role created by a dev-only script; never beta or prod. | Debugging invariants, scores, and seed output without ad-hoc `psql` juggling; faster root causes. | P1 / S | Add the read-only role to `infrastructure/docker-compose.yml` init, not to a migration. |
| [ ] C2. Browser for visual checks | Use Claude in Chrome (already available) or the built-in browser as the standard way to look at a changed screen: a rule in `CLAUDE.md` "screenshot the screen you changed before marking a UI TODO done". | No install; one rule plus the `/e2e` stack for a backend. | UI regressions are seen, not inferred from tests. | P1 / S | Keep screenshots out of the repo (or in the plan file only when they explain a decision). |
| [ ] C3. GitHub: keep `gh`, no MCP | `gh` already covers runs, releases, issues, and is signed in; an MCP server adds nothing here. | Nothing. Document it so nobody adds one. | Avoids a redundant tool surface. | P3 / S | Revisit if pull requests with review threads become the workflow. |
| [ ] C4. GCP read-only for beta diagnostics | A read-only service account (logging viewer, monitoring viewer, GKE viewer) for agent sessions, used by the `gcp-devkit` plugin's `gcp-forensics` agent and `gcloud-debug` skill. | Terraform: a `agent-readonly` SA with viewer roles in the beta project only; local `gcloud auth` with it for agent work. | Claude can read logs and pod state to diagnose beta without holding deploy rights. | P2 / M | Pairs with A6; prod stays out entirely. |
| [ ] C5. Error tracking or logging connector | When real beta users exist: a Sentry (or Cloud Logging) MCP so Claude can go from "an error happened" to the stack trace and the code. | Depends on the error-tracking decision (not made); the `engineering` plugin has a Datadog connector if that is chosen. | Incident triage with the trace in context. | P3 / M | Wait for users and the provider decision. |
| [ ] C6. Issue tracker: stay with TODO files | The `TODO.md`-per-feature system works well with agents (they read and update it in the same commit). Don't add Linear/Jira yet; if GitHub Issues are adopted, mirror only open decisions. | Decision noted here; `TODO_INDEX.md` kept current. | No second source of truth to drift. | P3 / S | Revisit with a second contributor. |

## D. Autonomy: scheduled and remote runs

| Item | Description | Setup | Benefit | Priority / effort | Notes |
| --- | --- | --- | --- | --- | --- |
| [ ] D1. Fund the weekly product docs | Add the `ANTHROPIC_API_KEY` repository secret (`docs/OWNER_TODO.md` 11) so the built workflow stops failing every Monday. | GitHub → Settings → Secrets → `ANTHROPIC_API_KEY`. | Non-technical product docs stay current with zero effort. | P1 / S | Owner step; billed per run. |
| [ ] D2. Weekly "state of the repo" | Extend `weekly-docs.yml`: CI pass rate and red time (`gh api`), deploys to beta, open TODOs by phase, decisions pending in `DECISIONS.md`, the week's commits in plain words. Commit to `docs/product/` or open a GitHub Discussion. | A prompt in `docs/product/PROMPTS.md` plus a small script collecting the numbers. | One page per week shows whether speed and quality are moving the right way (see section E). | P2 / M | Needs D1. |
| [ ] D3. Worktree by default | Set `worktree.baseRef: fresh` and ask (in `CLAUDE.md`) that every coding session starts with `EnterWorktree` when the shared checkout is dirty; one person-run cleanup of the 30+ stale worktrees under `.claude/worktrees/`. | Settings + a line in `CLAUDE.md`; `git worktree prune` and remove merged ones. | Parallel sessions stop stepping on each other; A1 becomes a rare last line of defense. | P1 / S | Note the worktree Bash guard rejects compound commands; use Write/Edit there. |
| [ ] D4. Claude Code GitHub Action | `anthropics/claude-code-action` (`/install-github-app`): `@claude` in issues and PRs, automatic review comments on pull requests. | Needs D1's key and the move to pull requests (`agent-tooling.md` principle 1). | Every change gets a review even when only one person is working. | P3 / M | Blocked on branch protection / PR decision (deferred in Phase 14). |
| [ ] D5. Nightly maintenance routine | A scheduled Claude run (cloud routine via `/schedule`, or a workflow) that reads yesterday's CI failures, `npm audit`, and NuGet vulnerabilities, and proposes a fix as a branch plus a summary; never pushes to `main`. | `/schedule` with a prompt scoped to read + branch; or a workflow with `claude-code-action`. | Dependency and flake debt stops accumulating silently. | P3 / M | Needs D4 or a cloud routine with repo access; agents open branches, people merge. |
| [ ] D6. Remote Control for away-from-desk work | Claude Code Remote Control on the dev machine so a session can be steered from the phone (checking CI, answering a question the session asked). | Enable in Claude Code; nothing in the repo. | Long verifications and deploy watches don't need a person at the keyboard. | P2 / S | Already listed in `agent-tooling.md`; just do it once. |

## E. Goals: know whether it is working

| Item | Description | Setup | Benefit | Priority / effort | Notes |
| --- | --- | --- | --- | --- | --- |
| [ ] E1. Written quality bar | Put the gates in one place in `CLAUDE.md`: every change has a test or says why not; `/verify` ran and its numbers are in the TODO entry; CI green before starting the next task; no open decision decided in code; UI changes screenshotted. | Edit `CLAUDE.md` Rules/Workflow (most exist; make them a checklist). | Agents and people are held to the same explicit bar; reviews get shorter. | P1 / S | Keep it short; the Stop hook (A7) echoes it. |
| [ ] E2. Speed and quality numbers | Track weekly: pushes to `main`, CI red time, reverts and fix-forward commits, time from push to healthy beta, e2e flake count, open TODO count per phase. | Collected by D2's script from `gh api` and the TODO files. | Shows whether AI support is paying off and where friction remains, with evidence rather than a feeling. | P2 / M | Start with three numbers; add more only if they change a decision. |
| [ ] E3. Hook and skill tests | Every hook has unit tests in `tools/agent-hooks/` (CI job "Agent hook tests" exists); every skill has a smoke run documented in its `SKILL.md`. | Extend the existing test pattern to A1–A7; add a `tools/claude-config/README.md` listing the install and the checks. | Guard rails that are themselves tested don't rot. | P1 / S | Follows the `test_guard_paths.py` pattern. |
| [ ] E4. Memory hygiene | Monthly: read `MEMORY.md`, delete stale facts (beta reset windows, old handoffs), move durable ones into repo docs so a second machine or person has them. | A calendar reminder; the repo docs are the destination (`DECISIONS.md`, `agent-tooling.md`). | Fewer wrong assumptions from an old memory; knowledge survives the machine. | P2 / S | Memory is per machine; the repo is the source of truth. |
| [ ] E5. Evaluate the agents once | Run `migration-reviewer` and `api-contract-checker` on three past commits with known issues (e.g. the deleted migration in a3b3964, a contract change without types regenerated) and record whether they catch them. | A short table in this file. | Confidence that the review agents find real problems, not just lint. | P2 / S | Adjust prompts from the misses. |

## Suggested order

1. A1, A2, D3 (shared-checkout safety: the one failure mode that has already cost a revert).
2. B1 `/verify`, B2 `/ship`, E1, E3 (same verification every time, written down and tested).
3. A3, C1, C2, D1 (contract follow-through, read-only database, screenshots, docs workflow funded).
4. B3–B7, A4–A7, C4, D2, D6, E2, E4, E5.
5. C5, D4, D5 when pull requests, real users, or a second contributor arrive.
