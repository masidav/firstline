# TODO — Backend API service

Use `- [x]` only after the task is implemented and verified. This is the service-wide checklist; each feature under
`src/features/<feature>/` has its own `TODO.md`. Audited 2026-11-06 against the code: every item below names where
it lives and what was run.

## Implementation checklist
- [x] Inspect existing API framework, route registration, middleware, and error-handling conventions before changes.
      *ASP.NET Core minimal API (.NET 10), one `MapX()` per feature on the `/v1` group (`Program.cs`); conventions
      in `README.md` here and `ARCHITECTURE.md`.*
- [x] Establish configuration loading and validate required environment variables at startup. *Environment variables
      and, in Kubernetes, one file per key in `Secrets__Directory` (`src/Infrastructure/SecretsDirectory.cs`). The API
      refuses to start without `ConnectionStrings__Database` (`Program.cs`), a signing key of 32+ bytes
      (`AuthOptions`, `ValidateOnStart`), `App__PublicOrigin` outside Development (`PublicOrigin.cs`), or with
      `Auth__DevSignIn` outside Development (`DevSignInGuard`). Verified: `PlaceholderSecretTests`, `DevSignInTests`,
      `PublicOriginTests`.*
- [x] Implement consistent request validation and structured error responses. *RFC 9457 problem+json with a stable
      `code` from `openapi.yaml` (`ApiProblemException`, `src/Infrastructure/Problems.cs`); 422 carries `errors`
      (`field`, `message`) built per endpoint (`FieldError`). Every response in the API tests is checked against the
      contract (`tests/FandomScore.Tests/Contract`).*
- [x] Implement authentication middleware and server-side authorization checks. *Bearer access tokens
      (`src/features/authentication/AuthSetup.cs`, ADR 0003); the `/v1` group requires sign-in unless an endpoint says
      `AllowAnonymous()`; staff endpoints add `RequireAuthorization(AuthSetup.StaffPolicy)`; ownership is checked in
      SQL (`WHERE user_id = @me`) rather than after loading. Verified: `AuthApiTests`, the 401/403 cases in each
      feature's tests.*
- [x] Add request IDs, safe structured logging, and health/readiness endpoints. *JSON console logs outside Development
      (`appsettings.json`) with the hosting scope (request id, path) included since 2026-11-06; log lines carry ids
      and counts, never tokens, addresses, or free text (`CLAUDE.md`). `GET /v1/health` is the liveness probe and the
      load balancer's health check; it answers without touching the database. There is no separate readiness probe:
      a pod that can't reach the database still reports healthy and fails requests with 503, which the uptime and
      error-rate alerts catch (`infrastructure/RUNBOOK.md`). Known gap, acceptable for now.*
- [x] Apply rate limiting to authentication, artist search/add, upload, and other abuse-sensitive routes. *Security
      pass 2026-10-28: a global per-caller ceiling plus named policies (`src/Infrastructure/AbuseLimits.cs`), sign-in
      per client address (`AuthSetup.SignInRateLimit`), uploads and artist adds with their own daily counts in SQL
      (`track_artist()`, upload slots), 1 MB request bodies unless an endpoint raises it. Verified: `RateLimitTests`.*
- [x] Register feature routes without creating duplicate endpoints. *The contract tests compare the API's routes with
      `openapi.yaml` both ways, so an unlisted or doubled route fails the suite
      (`tests/FandomScore.Tests/Contract/ContractCoverageTests.cs`).*
- [x] Ensure long-running work is queued instead of executed inside request handlers. *`enqueue_job()` in the same
      transaction (ADR 0004); the worker runs it (`workers/README.md`). Requests do at most one provider call
      (artist search, Spotify connect) and never wait on sync, scoring, redaction, or notifications.*
- [x] Document API contracts and test success, validation-failure, unauthorized, and server-error paths.
      *`openapi.yaml` is the contract; each feature's tests cover 200/201, 422, 401/403, and 404/409 for its
      endpoints; `ContractCheckedFactory` fails any response (error responses included) that doesn't match. Server
      errors become problem+json 500 through `UseExceptionHandler` without the exception text.*

## Verification
- [x] Add or update automated tests for the behavior above. *`tests/FandomScore.Tests` (381 test methods on 2026-11-06,
      one class per feature plus `Contract/`).*
- [x] Run the relevant tests and record actual results. *Recorded per feature in `PROJECT_TODO.md`; CI runs the suite
      on every push to `main` (`.github/workflows/ci.yml`). Last full local run: 2026-11-06, see the commit "Staff
      notices by email behind IEmailSender, retention switches, TODO audit".*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations. *`README.md` here
      rewritten 2026-11-06 from the real layout; feature READMEs under `src/features/*/README.md` (31).*
- [x] Update `PROJECT_TODO.md` when this feature reaches a verified milestone. *Done per feature as it landed
      (Phases 1 to 15).*
