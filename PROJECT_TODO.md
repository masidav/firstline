# Fandom Score — Master Project TODO

This checklist tracks milestones. Detailed tasks live in each feature's `TODO.md`.
Only mark a task complete after implementing and verifying it.

## Product direction (confirmed 2026-10-18)
The product is a **fan-first guaranteed ticketing platform** (see `executive_summary.txt`). Promoters and
artists set aside a share of each show's tickets for verified top fans, at face value and non-transferable,
before the public sale. Fans pay no scalper markup.

The fandom score, album journal, and proof-of-fandom work (Phases 1–7) is the **fan verification engine**
that decides eligibility. Phases 10–13 add the allocation platform, anti-fraud, hosting, and launch.
Because scores now decide who gets tickets, every scoring signal must be treated as an attack surface.

## Phase 0 — Inspect and preserve existing work
- [x] Inspect the repository, README, database schema, migrations, tests, and current entry points.
- [x] Record existing functionality and known gaps before changing files. See [inventory](docs/REPOSITORY_INVENTORY.md).
- [ ] Confirm the actual backend language/framework and mobile-app setup. Backend: C#/.NET 10, see [ADR 0001](docs/adr/0001-backend-csharp-dotnet.md). Mobile app not started.
- [x] Identify any conflicting, duplicated, or undocumented architecture.
- [ ] Confirm the current schema matches the product requirements; document differences rather than silently changing decisions.

Details: [Project setup](docs/TODO.md)

## Phase 1 — Foundation
- [ ] Establish repository structure without duplicating existing code.
- [x] Document environment variables and provide a safe `.env.example`. See [Development](docs/DEVELOPMENT.md#configuration).
- [x] Establish local development and test commands. See [Development](docs/DEVELOPMENT.md).
- [ ] Verify PostgreSQL connectivity and existing schema.
- [ ] Establish API error handling, request validation, logging, and configuration.
- [x] Establish automated checks and baseline tests. (Backend only: CI build with analyzers + migration, invariant, and API tests.)

Details: [API](services/api/TODO.md) · [Database](database/TODO.md) · [Tests](tests/TODO.md) · [Infrastructure](infrastructure/TODO.md)

## Phase 2 — Identity and access
- [x] Implement authentication and session lifecycle. Apple/Google sign-in, rotating refresh tokens with reuse detection, sign-out ([ADR 0003](docs/adr/0003-authentication.md)). Verified 2026-10-18: `dotnet test` 27/27.
- [x] Implement user profile and authorization rules. `/me` read, update, and delete; bearer auth on all of `/v1`; staff policy for `/admin` (checked per request). Verified 2026-10-18: `dotnet test` 36/36.
- [ ] Implement account recovery and account deletion policy. Recovery is provider sign-in (no passwords). Deletion request done; content retention is still an open decision.
- [x] Apple follow-ups: server-to-server notifications (`POST /v1/webhooks/apple`) and Apple sign-in revocation on account deletion. Verified 2026-10-18: AppleTests 8/8.

Details: [Authentication](services/api/src/features/authentication/TODO.md) · [Users](services/api/src/features/users/TODO.md) · [Privacy](services/api/src/features/privacy/TODO.md)

## Phase 3 — Tracked artists and catalog
- [ ] Implement artist search with tracked/untracked status.
- [ ] Implement shared tracked-artist addition and daily limit.
- [ ] Implement asynchronous artist onboarding and failure recovery.
- [ ] Load album/track catalog and seed a baseline before activation.

Details: [Artists](services/api/src/features/artists/TODO.md) · [Artist search UI](apps/mobile/src/features/artist-search/TODO.md) · [Artist onboarding worker](workers/artist-onboarding/TODO.md)

## Phase 4 — Spotify data
- [ ] Implement Spotify OAuth and secure token storage.
- [ ] Implement initial and scheduled syncs.
- [ ] Filter to tracked artists before persisting signals.
- [ ] Handle API limits, token expiry, retries, and sync status.

Details: [Spotify API feature](services/api/src/features/spotify-sync/TODO.md) · [Spotify integration](services/api/src/integrations/spotify/TODO.md) · [Spotify mobile UI](apps/mobile/src/features/spotify/TODO.md) · [Sync worker](workers/spotify-sync/TODO.md)

## Phase 5 — Scores and ranking
- [ ] Implement append-only points ledger and configurable scoring rules.
- [ ] Implement raw score calculation and diminishing returns.
- [ ] Implement synthetic baseline generation and real/synthetic percentile blending.
- [ ] Store and refresh computed scores for active artists only.
- [ ] Label estimates in the UI and test score edge cases.

Details: [Scoring API](services/api/src/features/scoring/TODO.md) · [Scoring worker](workers/scoring/TODO.md) · [Fandom score UI](apps/mobile/src/features/fandom-score/TODO.md)

## Phase 6 — Journal and community
- [ ] Implement album metadata cache and album logs.
- [ ] Implement ratings, reviews, relistens, visibility, and offline sync.
- [ ] Implement ordered album lists.
- [ ] Implement comments, replies, anti-abuse rules, and reward reversals.

Details: [Album journal API](services/api/src/features/album-journal/TODO.md) · [Journal UI](apps/mobile/src/features/album-journal/TODO.md) · [Lists API](services/api/src/features/lists/TODO.md) · [Lists UI](apps/mobile/src/features/lists/TODO.md) · [Comments API](services/api/src/features/comments/TODO.md) · [Comments UI](apps/mobile/src/features/comments/TODO.md)

## Phase 7 — Proof of fandom
- [ ] Implement secure upload and quarantine.
- [ ] Implement verification checks, staff review, audit trail, and status transitions.
- [ ] Award points only after verification; reverse them if a verified item is later rejected.
- [ ] Redact sensitive barcodes/QR codes before archival.
- [ ] Enforce archive safety and retention policies.

Details: [Submissions API](services/api/src/features/submissions/TODO.md) · [Submission UI](apps/mobile/src/features/submissions/TODO.md) · [Verification](services/api/src/features/verification/TODO.md) · [Verification worker](workers/submission-verification/TODO.md) · [Redaction worker](workers/image-redaction/TODO.md)

## Phase 8 — Sharing and commerce
Lower priority under the new direction: ticket purchases now flow through allocations (Phase 11), not outbound links.
- [ ] Generate score cards and native sharing flows.
- [ ] Display artist, ticket, and merch links.
- [ ] Track outbound clicks without misrepresenting clicks as purchases.
- [ ] Integrate purchase confirmation only if supported by the selected partner.

Details: [Sharing API](services/api/src/features/sharing/TODO.md) · [Sharing UI](apps/mobile/src/features/sharing/TODO.md) · [Ticketing API](services/api/src/features/ticketing/TODO.md) · [Ticketing UI](apps/mobile/src/features/ticketing/TODO.md)

## Phase 9 — Notifications, moderation, release
- [ ] Implement opt-in push notifications and preferences.
- [ ] Implement reports, blocking, staff moderation, and takedown handling.
- [ ] Complete privacy review, deletion flows, and retention rules.
- [ ] Complete integration, end-to-end, security, and performance testing.
- [ ] Document deployment, backups, monitoring, and recovery.
- [ ] Verify current external API terms and app-store requirements before release.

Details: [Notifications](services/api/src/features/notifications/TODO.md) · [Moderation](services/api/src/features/moderation/TODO.md) · [Release tests](tests/TODO.md) · [Infrastructure](infrastructure/TODO.md)

## Phase 10 — Fan verification and anti-fraud
- [ ] Define the eligibility model: which signals count, their weights, and minimum account/history age.
- [ ] Detect streaming-farm and bot behavior (play patterns, account age, device and payment signals, shared-device clusters).
- [ ] Add verification sources: Apple Music (MusicKit), direct-to-consumer store purchases, verified attendance history.
- [ ] Bind one person to one account (phone or ID verification at allocation time) without over-collecting personal data.
- [ ] Add fraud review tooling, an appeals process, and audit logging for every eligibility decision.
- [ ] Red-team the scoring rules before each pilot; record known gaming strategies and mitigations.

Details: [Fraud](services/api/src/features/fraud/TODO.md) · [Apple Music](services/api/src/integrations/apple-music/TODO.md)

## Phase 11 — Promoter allocation platform (B2B)
- [ ] Promoter and artist organizations with staff roles and permissions.
- [ ] Events and allocation pools: share of inventory, eligibility thresholds, registration window.
- [ ] Fan registration for an allocation and a fair, auditable selection method (ranked, lottery, or tiered).
- [ ] Non-transferable reservation tokens, delayed delivery, and face-value exchange for fans who can't attend.
- [ ] Primary ticketing integration (Ticketmaster, AXS, SeatGeek): reserve seats from the pool and hand off checkout.
- [ ] Promoter web dashboard: set up allocations, monitor registration, export reports.
- [ ] First-party fan data sharing with artists/promoters, only with explicit fan consent.
- [ ] Billing: per-event licence fee or take-rate for promoters; fan-side fee if adopted.
- [ ] Reporting for the pilot case study: sell-through, scalping complaints, empty seats.

Details: [Organizations](services/api/src/features/organizations/TODO.md) · [Allocations](services/api/src/features/allocations/TODO.md) · [Primary ticketing](services/api/src/integrations/primary-ticketing/TODO.md) · [Promoter dashboard](apps/promoter-web/TODO.md)

## Phase 12 — Hosting on GCP (GKE)
- [ ] Set up GCP projects per environment (dev, staging, prod), billing alerts, and IAM baseline.
- [ ] Provision infrastructure with Terraform: GKE Autopilot, Cloud SQL for PostgreSQL 16, Cloud Storage, Artifact Registry, Secret Manager.
- [ ] Containerize the API, migrator, and workers; publish images to Artifact Registry.
- [ ] Deploy with Kubernetes manifests (Kustomize overlays per environment); run migrations as a pre-deploy Job.
- [ ] Use Workload Identity for all pod access to GCP; no service-account key files.
- [ ] Restrict the quarantine bucket to the redaction workload only.
- [ ] Put the API behind Gateway API with managed TLS and Cloud Armor (WAF, bot and rate limiting).
- [ ] Plan for on-sale traffic spikes: autoscaling, pre-scaling before scheduled on-sales, and load tests.
- [ ] Set up CI/CD, logging, monitoring, alerting, backups with point-in-time recovery, and a tested restore.

Details: [ADR 0002](docs/adr/0002-hosting-gcp-gke.md) · [Infrastructure](infrastructure/TODO.md)

## Phase 13 — UI testing and launch
- [ ] Mobile component tests (Jest + React Native Testing Library) and end-to-end flows (Maestro) in CI.
- [ ] Promoter dashboard end-to-end tests (Playwright) and accessibility checks.
- [ ] API contract tests against `openapi.yaml`; load tests for on-sale registration spikes.
- [ ] Staging environment with synthetic data and test accounts for every external provider.
- [ ] Beta distribution: TestFlight and Google Play closed testing.
- [ ] App Store and Google Play readiness: privacy labels, data safety form, in-app account deletion, Sign in with Apple.
- [ ] Staged rollout: internal, closed alpha, one pilot show, public beta, general availability.

Details: [UI testing and launch plan](docs/plans/ui-testing-and-launch.md)

## Phase 14 — Developer tooling and remote code management
Can run in parallel with any phase; the first two items unblock the rest.
- [x] Put the repo under git and push to a private remote (GitHub: `masidav/firstline`).
- [ ] Branch protection on `main` and pull-request workflow. *Deferred: committing directly to `main` for now.*
- [x] Add CI that runs `dotnet build` and `dotnet test` (and later mobile/web checks) on every pull request. (`.github/workflows/ci.yml`; first run green 2026-10-18.)
- [x] Add a project `CLAUDE.md` with build/test commands, conventions, and the "never edit an applied migration" rule.
- [x] Add Claude Code project agents (`.claude/agents/`) for recurring work, e.g. a migration reviewer and an OpenAPI contract checker.
- [x] Add Claude Code hooks (`.claude/settings.json`): run `dotnet build` after edits, block edits to applied migrations and to `.env`/secrets. Scripts in `tools/agent-hooks/` (tested in CI).
- [ ] Set up remote code management: Claude Code Remote Control / Claude Code on the web, and GitHub Actions for agent-driven pull requests.
- [ ] *Deferred.* Set up OpenClaw for remote control from chat: run it on a dedicated machine or VM (not a laptop with personal data),
      with access limited to this repo, a scoped GitHub token, no production credentials, and only reviewed skills.
- [ ] Require every agent change (Claude Code, OpenClaw, CI bots) to land through a reviewed pull request; agents never push to `main` or deploy to prod. *Deferred with branch protection.*
- [x] Document how to start, stop, and audit remote agent sessions, and how to revoke their access. See [Agent tooling](docs/agent-tooling.md).

Details: [Agent tooling](docs/agent-tooling.md) · [Developer tooling](docs/TODO.md)

## Cross-cutting requirements
- [ ] Keep all score awards idempotent and ledger-based.
- [ ] Do not store untracked-artist listening data.
- [ ] Do not archive unredacted submission files.
- [ ] Keep secrets out of source control and logs.
- [ ] Enforce authorization and visibility at the API boundary.
- [ ] Add tests for failure cases, concurrency, and duplicate requests.
- [ ] Document decisions that remain proposed or open in the source README.

## Open decisions
- [ ] Confirm backend framework and job-queue choice. Framework confirmed (ASP.NET Core, [ADR 0001](docs/adr/0001-backend-csharp-dotnet.md)); job library open.
- [ ] Confirm album catalog provider and its caching/artwork terms.
- [ ] Confirm ticketing partner and purchase-confirmation capability.
- [ ] Confirm archive meaning and user-facing archive requirements.
- [ ] Confirm verification thresholds and baseline blend parameters.
- [ ] Define data retention and account deletion behavior.
- [x] Account linking: no. One email per account; a second provider with the same email gets `409 email_in_use`. (Decided 2026-10-18.)
- [ ] Confirm exact Spotify signals available under current API access.
- [ ] Decide how to handle co-credited albums and collaborations.
- [ ] Confirm the product and company name (summary proposes TrueFan Access or VerifyTicket; repo uses Fandom Score).
- [ ] Choose the primary ticketing model: white-labeled checkout through ticketer APIs, or direct allocation links.
- [ ] Confirm which primary ticketers will grant API access for reserved inventory, and on what terms.
- [ ] Confirm Spotify's developer terms allow using listening data to decide ticket eligibility, and that the
      app qualifies for extended API quota. Same check for Apple Music.
- [ ] Check laws on non-transferable tickets and ticket resale in each launch market (some US states restrict non-transferable tickets).
- [ ] Decide the selection method for oversubscribed allocations (ranked by score, lottery among eligible fans, or tiers).
- [ ] Decide on the fan-side fee and whether App Store / Google Play payment rules apply to it.
- [ ] Choose the first pilot artist(s) and promoter.
