# Agent tooling and remote code management

Status: 2026-10-18. Covers Claude Code configuration, remote access, and OpenClaw. Phase 14 in `PROJECT_TODO.md`.

## Principles
1. **Agents never deploy to production or hold production credentials.** For now (2026-10-18), changes are
   committed directly to `main`. Once more people or remote agents contribute, switch to reviewed pull
   requests with branch protection on `main`.
   CI checks every push in the meantime, including the applied-migration check.
2. **Guard rails live in the repo and in CI**, so they apply to every agent and every person. Local hooks
   give fast feedback; CI is the backstop.
3. **Least privilege per agent.** Each remote agent gets its own credentials, scoped to this repo, with an expiry.

## What's in the repo

| Path | Purpose | Active? |
| --- | --- | --- |
| `CLAUDE.md` | Project instructions Claude Code loads every session: commands, layout, rules. | Yes |
| `tools/agent-hooks/guard_paths.py` | Blocks edits to migrations already on `main`, and any access to `.env` files. | Yes |
| `tools/agent-hooks/build_after_edit.py` | Builds the solution after `.cs`/`.csproj` edits and shows errors to Claude. Skips if no .NET SDK. | Yes |
| `tools/agent-hooks/test_guard_paths.py` | Tests for the guard (run in CI). | Yes |
| `tools/claude-config/settings.json` | Reviewed source of `.claude/settings.json` (hook configuration). | Installed |
| `tools/claude-config/agents/*.md` | Reviewed source of `.claude/agents/`: `migration-reviewer`, `api-contract-checker`. | Installed |
| `.github/workflows/ci.yml` | Build and tests; **fails if a migration already on `main` changed** (on pushes and pull requests); hook tests. | Yes |

## Install the Claude Code config (one time, by a person)
Claude Code doesn't let an agent change its own hooks and agents, so a person installs them:
```bash
mkdir -p .claude/agents
cp tools/claude-config/settings.json .claude/settings.json
cp tools/claude-config/agents/*.md .claude/agents/
git add .claude && git commit -m "Install Claude Code hooks and agents"
```
Then start a new Claude Code session and run `/hooks` and `/agents` to confirm they're loaded. Quick checks:
- Ask Claude to add a comment to `database/migrations/0001_initial_schema.sql`. It should be blocked.
- Ask it to read `.env`. It should be blocked.

Keep `tools/claude-config/` as the reviewed source. Change it there first, then copy.
Requirements: `python3` (3.10+) and `git` on the machine running Claude Code.

## Remote code management

| Option | Where the code runs | Good for |
| --- | --- | --- |
| **Claude Code Remote Control** | Your own machine; you steer the session from claude.ai or the Claude mobile app | Continuing local work while away from the desk |
| **Claude Code on the web** (claude.ai/code) | Anthropic-managed cloud sandbox with a clone of the GitHub repo | Kicking off tasks from anywhere; results arrive as pull requests |
| **Claude Code GitHub Actions** (`anthropics/claude-code-action`, set up with `/install-github-app`) | GitHub Actions runners | `@claude` in issues and PRs; automated reviews |
| **OpenClaw** | A dedicated VM you control; you message it from a chat app | Chat-driven tasks and scheduled chores (see below) |

All four honor `CLAUDE.md` and the repo's CI. Local hooks run wherever `.claude/settings.json` is present in
the checkout. Check the Claude Code docs for current setup commands, since these features change often.

## OpenClaw setup (deferred)
Not being set up for now. Revisit once pull requests and branch protection are in place, since OpenClaw
should only ever be able to open pull requests, never push to `main`.

OpenClaw is a self-hosted agent that you control from chat apps and extend with "skills". It's powerful
remote access to whatever machine it runs on, and its public skill registry has had malicious skills,
so set it up deliberately:

1. **Dedicated host:** a small VM (e.g. a separate GCP project, not the prod project) or a spare machine.
   Never your laptop and never a machine with personal data or production access.
2. **Run as a non-admin user.** Install from the official repository and keep it updated.
3. **Network:** keep the OpenClaw gateway bound to localhost and reach it over a private network such as
   Tailscale. Never expose it to the public internet.
4. **Who can command it:** allow only your own chat account(s), using its sender pairing or allowlist.
   Treat links and documents you forward to it as untrusted input.
5. **GitHub access:** a **fine-grained personal access token** (or a GitHub App) limited to `masidav/firstline`
   with only *Contents: read/write* and *Pull requests: read/write*. Give it a 90-day expiry. Branch protection
   then stops it from pushing to `main`.
6. **No other secrets:** no GCP credentials, no Spotify or ticketing keys, no `.env` on the host.
7. **Skills:** enable only skills you have read. Pin versions, and prefer writing small skills of your own.
8. **Coding:** have OpenClaw drive Claude Code inside a clone of the repo with the hooks installed, so
   `CLAUDE.md`, the guard hooks, and CI all apply to its work.

## Start, stop, audit, revoke

| Action | How |
| --- | --- |
| See what agents changed | GitHub pull request history; every agent PR names its agent in the branch or description. |
| Stop a Claude Code session | Ctrl+C locally; stop the session in claude.ai for web or Remote Control sessions. |
| Stop OpenClaw | Stop its service on the VM, or shut down the VM. |
| Revoke GitHub access | GitHub → Settings → Developer settings → revoke the token; or uninstall the GitHub App. |
| Revoke Claude GitHub app | Repo → Settings → GitHub Apps → Claude → Configure → remove the repo. |
| Rotate after an incident | Revoke all agent tokens, review open PRs from agents, then re-issue with fresh expiries. |

Review agent access every quarter: list active tokens, apps, and hosts, and remove anything unused.
