# Backend API service

`FandomScore.Api` (this folder): the HTTP API every client uses, an ASP.NET Core minimal API on .NET 10. The
contract is `openapi.yaml` at the repository root; the fan app and the promoter dashboard generate their types from it.

```bash
dotnet run --project services/api        # http://localhost:5271/v1/health; settings from .env (docs/DEVELOPMENT.md)
dotnet test FandomScore.slnx             # the API tests start PostgreSQL 16 in Docker (Testcontainers)
```

## Layout
| Path | Contents |
| --- | --- |
| `Program.cs` | Host setup in one file: data sources, auth, limits, CORS, forwarded headers, then one `v1.MapX()` line per feature. |
| `src/features/<feature>/` | One folder per product feature: `XEndpoints.cs` (routes), SQL in the endpoint code (Dapper), a `README.md` with the feature's rules and tests, and a `TODO.md`. |
| `src/Infrastructure/` | What every feature shares: `Problems.cs` (problem+json), `AbuseLimits.cs` (rate limits), `Idempotency.cs` (`Idempotency-Key`), `PageCursor.cs`, `SignedUrls.cs`, `PublicOrigin.cs`, `SecretsDirectory.cs`, `WebClients.cs` (CORS), `ForwardedHeadersSetup.cs`. |
| `src/integrations/` | Provider clients the API owns (album catalog, object storage, OCR and ticketing placeholders). Spotify, Apple Music, Tidal, storage, and notification text live in `packages/` because the worker shares them. |
| `appsettings*.json` | Logging only. Every real setting comes from the environment (`.env.example`); secrets never from files in the repo. |

## Conventions
- **Contract first.** Every endpoint change updates `openapi.yaml` in the same commit; the tests build the app with
  `ContractCheckedFactory`, which fails any response that doesn't match, and compare the routes with the contract
  both ways (`tests/FandomScore.Tests/Contract`). JSON is snake_case.
- **Sign-in by default.** The `/v1` group requires a bearer access token; an endpoint opts out with
  `AllowAnonymous()`. Staff endpoints (`/v1/admin/...`) add `RequireAuthorization(AuthSetup.StaffPolicy)`. Ownership is
  enforced in the SQL (`WHERE user_id = @me`), so a wrong id reads as 404, never as someone else's data. ADR 0003.
- **Errors are RFC 9457 problem+json** with a stable `code` listed in `openapi.yaml` (`ApiProblemException`). Field
  problems are 422 with `errors: [{field, message}]`. Unhandled exceptions become a 500 without the exception text.
- **Creating endpoints take `Idempotency-Key`**: the same key and body replays the stored response, a different body
  is `409 idempotency_conflict`; keys are per user and kept 24 hours (`src/Infrastructure/Idempotency.cs`).
- **Nothing slow in a request.** Syncs, scoring, draws, redaction, notifications, and cleanup are queued with
  `enqueue_job()` in the same transaction and run by the worker (`workers/README.md`, ADR 0004). A request makes at
  most one provider call (artist search, a sign-in token check, a Spotify connect).
- **Limits** (`src/Infrastructure/AbuseLimits.cs`, security pass 2026-10-28): a global per-caller ceiling, named
  policies for calls that cost provider quota or render images, sign-in per client address, uploads and artist adds
  counted per day in SQL, 1 MB request bodies unless an endpoint raises it. 429 and 503 carry `Retry-After`.
- **Links are absolute from `App__PublicOrigin`**, never from the request's `Host` header (required outside
  Development). Behind the load balancer the client address comes from `X-Forwarded-For` only for the proxies in
  `ForwardedHeaders__KnownProxies`.
- **Database rules** (`CLAUDE.md`): migrations are never edited once on `main`; `score_events` is append-only; only
  tracked artists are stored; unredacted proof is never archived.

## Configuration
Required: `ConnectionStrings__Database`, `Auth__SigningKey` (32+ random bytes, base64), and outside Development
`App__PublicOrigin`. Providers are off until their settings exist (Apple and Google client ids, `Spotify__*`,
`MusicProviders__*`). `Auth__DevSignIn=true` is for local development only and refuses to start anywhere else.
The full list with explanations: `.env.example`; local setup: `docs/DEVELOPMENT.md`; Kubernetes: secrets arrive as
files in `Secrets__Directory` (`infrastructure/k8s`).

## Observability
- `GET /v1/health` (no sign-in): the liveness probe and load-balancer health check. It answers without the database;
  there is no separate readiness probe, so a pod that lost the database reports healthy and fails requests with 503,
  which the uptime and error-rate alerts catch (`infrastructure/RUNBOOK.md`).
- Logs are JSON on the console outside Development, with the request id and path in scope (`appsettings.json`). Log
  lines carry ids and counts, never tokens, addresses, or free text (`CLAUDE.md`).
- Dashboards and alerts for the API (latency, 5xx, pods) are in Terraform (`infrastructure/`), Phase 12.

## Tests
`tests/FandomScore.Tests`: one class per feature (`<Feature>ApiTests`), each on its own migrated database, signing in
through the real `POST /v1/auth/sessions` with a fake identity-token validator. Each covers the success, validation,
unauthorized or forbidden, and not-found or conflict paths of its endpoints. `Contract/` holds the response and
route checks; `database/tests/invariants.sql` the trigger and constraint checks.

## Known limitations
- No readiness probe (above).
- OCR of ticket submissions is not built (`src/integrations/ocr/TODO.md`); `submissions.extracted` stays empty.
- Primary ticketing (reserving seats with a ticketer) is a placeholder (`src/integrations/primary-ticketing`); tickets
  today are allocation reservations with a signed token.
