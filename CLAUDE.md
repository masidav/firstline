# CLAUDE.md

Fan-first guaranteed ticketing platform: verified top fans get face-value, non-transferable tickets before the
public sale. The fandom score (Spotify signals, album journal, proof-of-fandom submissions) decides eligibility.
Direction: `executive_summary.txt`. Roadmap: `PROJECT_TODO.md`, with per-feature `TODO.md` files.

## Commands
```bash
dotnet build FandomScore.slnx                 # analyzer warnings are errors
dotnet test FandomScore.slnx                  # needs Docker (Testcontainers starts PostgreSQL 16)
dotnet run --project database                 # apply migrations (needs ConnectionStrings__Database)
dotnet run --project services/api             # http://localhost:5271/v1/health
dotnet run --project workers                  # background jobs (Postgres queue)
```
Local setup: `docs/DEVELOPMENT.md`. If the .NET SDK isn't installed, run these in `mcr.microsoft.com/dotnet/sdk:10.0`.

## Layout
- `services/api`: ASP.NET Core minimal API (.NET 10). Feature code in `src/features/<feature>`.
- `workers`: the worker process. Job handlers (`IJobHandler`) in `workers/<feature>`; queue in `workers/jobs` (ADR 0004).
- `database/migrations`: plain SQL migrations, applied in name order by DbUp (`database/`).
- `database/tests/invariants.sql`: database invariant tests, run by the test suite.
- `tests/FandomScore.Tests`: xUnit tests (migrations, invariants, API).
- `openapi.yaml`: the API contract. The API implements it; clients are generated from it.
- Decisions: `docs/adr/`. Plans: `docs/plans/`.

## Rules
- **Never edit or delete a migration that exists on `main`.** Add a new `database/migrations/NNNN_name.sql`.
  CI and a hook enforce this.
- **The points ledger (`score_events`) is append-only.** Reverse awards with `reverse_points()`; never update or delete.
- **Only tracked artists** are synced, scored, journaled, or verified. Don't store untracked-artist listening data.
- **Never archive unredacted submission files.**
- **No secrets in the repo, in `appsettings*.json`, or in logs.** Configuration comes from environment variables;
  `.env.example` holds placeholders only. Never read or edit `.env`.
- Errors are RFC 9457 problem+json with a stable `code` from `openapi.yaml` (`ApiProblemException`).
- Every `/v1` endpoint requires sign-in unless it calls `AllowAnonymous()`. Staff-only (`/admin`) endpoints add
  `RequireAuthorization(AuthSetup.StaffPolicy)`.
- JSON is snake_case. Every API change updates `openapi.yaml` in the same pull request.
- New triggers or constraints get a check in `database/tests/invariants.sql`.
- Work that can wait or retry goes to the job queue: `enqueue_job()` in the same transaction. Handlers are idempotent.
- Mark a TODO `[x]` only after it is implemented and verified; record what was actually run.
- Open product decisions are listed in `PROJECT_TODO.md`. Don't silently decide them in code.

## Workflow
- For now, changes are committed directly to `main` (no pull requests or branch protection yet).
  Commit and push when a piece of work is done and verified; no need to wait to be asked.
- Run `dotnet build` and `dotnet test` before committing, and report the actual results.
  CI (`.github/workflows/ci.yml`) runs on every push to `main`; check that it passes.
- Hooks in `.claude/settings.json` block edits to applied migrations and `.env` files. Their reviewed source
  is `tools/claude-config/`; change it there first, then copy.
