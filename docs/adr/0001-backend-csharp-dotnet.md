# ADR 0001: Backend in C# on .NET 10

- **Status:** Accepted (2026-10-18)
- **Scope:** API service, migrations, and future background workers. The mobile app stays React Native/Expo.

## Decision
- **Runtime:** .NET 10 (LTS), pinned by `global.json` (roll forward within 10.0 feature bands).
- **API:** ASP.NET Core minimal APIs (`services/api`), built to the existing `openapi.yaml` contract.
- **Data access:** Npgsql with Dapper. Entity Framework Core does **not** own the schema.
- **Migrations:** plain SQL files in `database/migrations`, applied in name order by DbUp (`database/`).
  The schema relies on triggers and PL/pgSQL functions, so SQL is the source of truth.
- **Errors:** RFC 9457 `application/problem+json` with a stable `code` (see `openapi.yaml`, `Problem`).
- **Tests:** xUnit with Testcontainers (PostgreSQL 16). Database invariants in `database/tests/invariants.sql`
  run as part of the suite.
- **Deployment shape:** one database and one codebase, deployed as separate processes: `api`, `worker`
  (Spotify sync, onboarding, scoring, notifications), and `redaction` (the only process with access to the
  quarantine bucket). Not microservices with separate databases: the points ledger depends on cross-table
  triggers.

## Consequences
- `packages/shared-types` and `packages/validation` cannot share code with the backend. Generate the
  TypeScript client and types from `openapi.yaml` instead.
- Migrations are append-only: never edit a migration that has been applied anywhere; add a new one.
- If ImageSharp is used for redaction, it needs a commercial license above $1M annual revenue.
  OpenCvSharp or SkiaSharp avoid that.

## Still open
- ~~Background job library~~ Decided: a small Postgres queue, [ADR 0004](0004-postgres-job-queue.md).
- **OCR/vision provider** for submission verification.
