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
- [x] Implement account recovery and account deletion policy. Recovery is provider sign-in (no passwords). Deletion scrubs personal data at once; content is hard-deleted after 30 days (`accounts.purge_deleted` job). Verified 2026-10-18: `dotnet test` 45/45.
- [x] Apple follow-ups: server-to-server notifications (`POST /v1/webhooks/apple`) and Apple sign-in revocation on account deletion. Verified 2026-10-18: AppleTests 8/8.

Details: [Authentication](services/api/src/features/authentication/TODO.md) · [Users](services/api/src/features/users/TODO.md) · [Privacy](services/api/src/features/privacy/TODO.md)

## Phase 3 — Tracked artists and catalog
- [x] Implement artist search with tracked/untracked status. `GET /v1/artists/search`, Spotify app token
      (client credentials, ADR 0005, decided 2026-10-20). Verified 2026-10-20 with a faked Spotify: `dotnet test` 87/87.
- [x] Implement shared tracked-artist addition and daily limit (50 per user per UTC day, decided 2026-10-18; enforced by `track_artist()`).
      `POST /v1/artists`: name and image from Spotify, new artists start `onboarding`. Verified 2026-10-20 with a faked Spotify (ArtistApiTests).
- [x] Home screen search (mobile): type to filter tracked artists (`GET /v1/artists`), Enter asks Spotify and offers Add.
      Verified 2026-10-21: `dotnet test` 94/94, mobile `npm test` 33/33.
- [x] Check artist search and add against real Spotify (app token from the client secret). Verified 2026-10-21 on
      the local stack: `spotify_connect.py search radiohead` returned 10 real results; in the web app (Chrome,
      phone size) Enter on "radiohead" listed Spotify matches, Add tracked Radiohead as onboarding with its image.
- [x] Background job queue: Postgres `jobs` table and `workers/` process ([ADR 0004](docs/adr/0004-postgres-job-queue.md)). Verified 2026-10-18: JobQueueTests 9/9.
- [x] Implement asynchronous artist onboarding and failure recovery. Worker `artist.onboard` queued by `track_artist()`
      (migration 0007); retries with backoff, missing artists paused, `request_artist_onboarding()` re-queues. Verified 2026-10-21: `dotnet test` 109/109 (ArtistOnboardingJobTests 15, invariants).
- [x] Load album/track catalog and seed a baseline before activation. Spotify catalog per [ADR 0006](docs/adr/0006-album-catalog.md)
      with weekly `catalog.refresh`; provisional lognormal baseline (parameters still open, below). Verified 2026-10-21.

Details: [Artists](services/api/src/features/artists/TODO.md) · [Artist search UI](apps/mobile/src/features/artist-search/TODO.md) · [Artist onboarding worker](workers/artist-onboarding/TODO.md)

## Phase 4 — Spotify data
- [x] Implement Spotify OAuth and secure token storage (backend: PKCE code exchange, AES-GCM refresh tokens; [ADR 0005](docs/adr/0005-spotify-integration.md)). Verified 2026-10-18: `dotnet test` 67/67.
- [x] Implement initial and scheduled syncs (worker `spotify.sync`, hourly `spotify.schedule_syncs`, daily per user). Verified 2026-10-18 with a faked Spotify.
- [x] Filter to tracked artists before persisting signals. Verified 2026-10-18 (SpotifySyncJobTests).
- [x] Handle API limits, token expiry, retries, and sync status. Verified 2026-10-18 with a faked Spotify.
- [x] Check the flow end to end against real Spotify (development-mode app, allowlisted account), using
      `tools/dev/spotify_connect.py`. Verified 2026-10-20 on the local compose stack: real consent, `PUT
      /v1/me/spotify`, worker sync to `idle` with no error, 1 tracked artist found (liked 11, playlist 8,
      albums 11, top rank 17, recent 0).
- [ ] Mobile connect screen (see Spotify mobile UI).
- [x] A Spotify sync adds the fan's top artists that nobody tracks yet, so their page is lively right after
      connecting: tracked for the fan like `POST /v1/artists`, then onboarded and scored on the next sync
      (`SpotifyStatus.top_artists_added`, `GET /artists?added_by_me=true`; decided 2026-10-22, ADR 0005). Verified
      2026-10-22 with a faked Spotify (`SpotifySyncJobTests`) and in the fan app tests. "On Repeat" cannot be read
      by development-mode apps since Spotify's November 2024 API changes, so top artists are the source.

Details: [Spotify API feature](services/api/src/features/spotify-sync/TODO.md) · [Spotify integration](packages/spotify/TODO.md) · [Spotify mobile UI](apps/mobile/src/features/spotify/TODO.md) · [Sync worker](workers/spotify-sync/TODO.md)

## Phase 5 — Scores and ranking
- [x] Implement append-only points ledger and configurable scoring rules. Weights are relative shares in
      `scoring_rules`, an even split across the five Spotify signals (decided 2026-10-21); change them with
      `testScripts/scoringWeights.sh` or `PUT /v1/admin/scoring-rules/{key}`, and every artist is rescored.
- [x] Implement raw score calculation and diminishing returns. Formula: `workers/scoring/README.md`.
- [x] Score the wider Spotify signals (2026-10-24, migration 0015): top artists over three time ranges, top
      tracks, saved albums, early saves and tenure, playlist detail, follows (new scope `user-follow-read`), an
      hourly play ledger with 90-day plays and weekly streaks, catalog coverage, and deep cuts: 18 Spotify rules,
      even split. Verified: `dotnet test` 199/199 (invariants for every share), fan app 83/83. Not done from the
      review of 2026-10-24: live "listening now" check-ins (needs events to check against) and the Spotify data
      export upload (needs the submissions upload flow); both are listed under Open decisions / Phase 7.
- [x] Implement synthetic baseline generation and real/synthetic percentile blending.
- [x] Store and refresh computed scores for active artists only. Worker `scoring.score_artist` after syncs,
      weight changes, on request, and daily. Verified 2026-10-21: `dotnet test` 128/128.
- [x] Label estimates in the UI and test score edge cases. Score screen in the fan app (`/artist/{id}/score`):
      percentile, estimate label with the baseline share, what a percentile means, every signal with its count and
      share, onboarding/computing/unavailable states with polling and Refresh. Verified 2026-10-24: fan app
      typecheck and jest (`ScoreScreen.test.tsx`, `score.test.ts`).

Details: [Scoring API](services/api/src/features/scoring/TODO.md) · [Scoring worker](workers/scoring/TODO.md) · [Fandom score UI](apps/mobile/src/features/fandom-score/TODO.md)

## Phase 6 — Journal and community
- [x] Implement album metadata cache and album logs. Provider, caching, artwork, and attribution rules: [ADR 0006](docs/adr/0006-album-catalog.md).
      Albums and Journal endpoints with `Idempotency-Key` replay (migration 0009). Verified 2026-10-21: `dotnet test` (JournalApiTests 11, invariants).
- [ ] Implement ratings, reviews, relistens, visibility, and offline sync. (API side done 2026-10-21 with the logs above; followers-only
      logs show to followers since 2026-10-25 (follows, migration 0018); offline sync is the app's queue, still to build.)
- [ ] Implement ordered album lists.
- [ ] Implement comments, replies, anti-abuse rules, and reward reversals.

Details: [Album journal API](services/api/src/features/album-journal/TODO.md) · [Journal UI](apps/mobile/src/features/album-journal/TODO.md) · [Lists API](services/api/src/features/lists/TODO.md) · [Lists UI](apps/mobile/src/features/lists/TODO.md) · [Comments API](services/api/src/features/comments/TODO.md) · [Comments UI](apps/mobile/src/features/comments/TODO.md)

## Phase 7 — Proof of fandom
- [x] Implement secure upload and quarantine. Signed upload links, JPEG/PNG only, size and hash checks, EXIF/XMP/text
      metadata stripped on the server; files in local quarantine storage (migration 0012, branch `proof-feed`).
      Verified 2026-10-21: `dotnet test` 163/163 (ProofApiTests 6), clicked through in Chrome. Production storage and
      cleanup are the two items below.
- [ ] Implement verification checks, staff review, audit trail, and status transitions. *Staff review done (queue,
      approve/reject with a manual check row, app screen for staff); automated checks (hash, OCR, event match) not yet,
      so every submission goes to `needs_review`.*
- [x] Award points only after verification; reverse them if a verified item is later rejected. (Existing trigger; covered by ProofApiTests.)
- [ ] Photo storage for real hosting: the Cloud Storage quarantine bucket (ADR 0002) behind `IFileStore`, with signed
      upload and download URLs from the bucket instead of the API, Workload Identity, and limits on size and count.
      Locally, photos are on disk (`services/api/.local-storage/`).
- [ ] Clean up stored photos: delete upload slots that expired unused (and their files), files of withdrawn or purged
      submissions, and every file of a deleted account when it is purged (30 days, `purge_deleted_user`). Needs a
      worker job with storage access and tests that no file outlives its rows.
- [ ] Redact sensitive barcodes/QR codes before archival.
- [ ] Enforce archive safety and retention policies.

Details: [Submissions API](services/api/src/features/submissions/TODO.md) · [Submission UI](apps/mobile/src/features/submissions/TODO.md) · [Verification](services/api/src/features/verification/TODO.md) · [Verification worker](workers/submission-verification/TODO.md) · [Redaction worker](workers/image-redaction/TODO.md)

Fans can share verified merch and concert photos to the home feed (`PUT /submissions/{id}/share`); tickets and
screenshots are never shared. The home feed (`GET /feed`) is one vertical scroll mixing, at random, shared photos
and album logs from every fan, grab-bag songs, followers-only logs, and fandom stats from the fans you follow,
upcoming shows of your artists, and your own fan-ticket registrations (2026-10-25; `services/api/src/features/feed/README.md`).

## Phase 8 — Sharing and commerce
Lower priority under the new direction: ticket purchases now flow through allocations (Phase 11), not outbound links.
- [ ] Generate score cards and native sharing flows.
- [ ] Display artist, ticket, and merch links.
- [ ] Track outbound clicks without misrepresenting clicks as purchases.
- [ ] Integrate purchase confirmation only if supported by the selected partner.
- [ ] Load upcoming concerts, presale dates, and general-sale openings for tracked artists (source: open
      decision below). Feeds `/artists/{id}/events` and the home-screen concert cards.
- [x] Fan location preference: a primary hub city plus two backups, used to pick which shows to show. From a
      curated list of US and Canadian metros (migration 0010; `GET /v1/cities`, `PUT /v1/me/hub-cities`). Verified
      2026-10-21: `dotnet test` 157/157 (HubCityApiTests 13, invariants), app `npm test` 62/62, clicked through in Chrome.
- [x] Follow/follower relationships between fans (decided 2026-10-20): follow without approval, remove a
      follower; followers see followers-only logs, grab bags, and top fandom stats on the home feed (blocking not
      wanted for now, 2026-10-24). Migration 0018; `PUT/DELETE /users/{handle}/follow`, `GET /users?q=`,
      `/me/following`, `/me/followers`; app screen `/people` and Follow buttons on feed cards. Verified 2026-10-25:
      `dotnet test` (FollowApiTests 3, invariants), app Jest (PeopleScreen, Feed), clicked through in Chrome.
- [x] Grab bag: up to 5 favorite songs per fan, shared with followers by default, each linking to Spotify.
      Adding a song by an untracked artist tracks that artist (through `track_artist(..., 'grab_bag')`, outside the
      daily limit). Migration 0018; `/me/grab-bag/{position}`, `GET /tracks/search`; app screen `/grab-bag`.
      Verified 2026-10-25: `dotnet test` (GrabBagApiTests 3 with a faked Spotify, invariants), app Jest (GrabBagScreen).
      Bouncer door done 2026-10-25 (`GET /me/bouncer-door`, app `/bouncer-door`), a daily refresh of stored song titles
      and covers (`grab_bag.refresh_songs`, migration 0019), and seed data for follows and grab bags.
- [x] Home feed as one vertical scroll (2026-10-25): a seeded random mix of photos, album logs, followed fans' grab-bag
      songs and stats, upcoming shows, and the fan's registrations, dealt out by type so every page mixes them; infinite
      scroll, pull/Shuffle for a new mix. Verified: `dotnet test` (FeedApiTests 2), app Jest (Feed 7), Chrome at phone size.
- [x] Sign-up asks for a primary hub city (plus optional backups). Changes allowed once a week, with a warning.
      The app sends signed-in fans without a hub city to `/hub-cities` before anything else; the first choice can
      be changed once right away, then once a week.

Details: [Sharing API](services/api/src/features/sharing/TODO.md) · [Sharing UI](apps/mobile/src/features/sharing/TODO.md) · [Ticketing API](services/api/src/features/ticketing/TODO.md) · [Ticketing UI](apps/mobile/src/features/ticketing/TODO.md)

## Phase 9 — Notifications, moderation, release
- [ ] Implement opt-in push notifications and preferences.
- [x] Reports and staff takedowns (2026-10-24): fans report a shared photo or public album log (`POST /reports`);
      staff see reported posts grouped with their reasons (`/admin/reports`, app screen "Reported posts") and take the
      post down or dismiss the reports; every decision is recorded (`moderation_actions`). Migration 0014.
      Verified: `dotnet test` (ReportApiTests 3, invariants), app Jest (Feed, ModerationScreen), clicked through in Chrome.
- [ ] Blocking: not wanted for now (decided 2026-10-24); revisit with follows (Phase 8).
- [ ] Email staff when something needs them: a post is reported, proof is submitted for review, and allocation
      selection runs for a show (winners chosen). Needs an email provider (open decision) and a staff address list.
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
- [x] Events and allocation pools: share of inventory, eligibility thresholds, registration window. Staff-managed
      for now (`/v1/admin/events`, `tools/dev/allocations.py`); verified 2026-10-21, AllocationApiTests 16/16.
- [ ] Fan registration for an allocation and a fair, auditable selection: ranked by fandom score among fans
      whose three hub cities include the show's city, then other fans for unfilled seats; ties broken by a
      recorded random draw; a registration keeps the hubs it was made with (decided 2026-10-20).
      *Registration done 2026-10-21; selection done 2026-10-24 (migration 0017, `POST /v1/admin/allocations/{id}/selection`):
      hub fans first, snapshot raw score, seeded tiebreak, +1 seats. The app shows each fan's standing and says "most likely"
      only inside a 25-seat margin (decided 2026-10-24). Reservations and notifications are next.*
- [ ] Non-transferable reservation tokens, delayed delivery, and face-value exchange for fans who can't attend.
- [ ] Primary ticketing integration (Ticketmaster, AXS, SeatGeek): reserve seats from the pool and hand off checkout.
- [ ] Promoter web dashboard: set up allocations, monitor registration, export reports.
- [ ] First-party fan data sharing with artists/promoters, only with explicit fan consent.
- [ ] Billing: per-event licence fee or take-rate for promoters; fan-side fee if adopted.
- [ ] Reporting for the pilot case study: sell-through, scalping complaints, empty seats.

Details: [Organizations](services/api/src/features/organizations/TODO.md) · [Allocations](services/api/src/features/allocations/TODO.md) · [Primary ticketing](services/api/src/integrations/primary-ticketing/TODO.md) · [Promoter dashboard](apps/promoter-web/TODO.md)

## Phase 12 — Hosting on GCP (GKE)
Code for all of this exists in `infrastructure/` and `Dockerfile` (2026-10-21; Terraform validated, manifests
built, images built locally). Nothing is applied to a GCP project yet; `infrastructure/README.md` has the steps.
- [ ] Set up GCP projects for beta and prod (dev runs locally; decided 2026-10-24), billing alerts, and IAM baseline. (Budgets and IAM in Terraform; projects not created.)
- [ ] Provision infrastructure with Terraform: GKE Autopilot, Cloud SQL for PostgreSQL 16, Cloud Storage, Artifact Registry, Secret Manager. (Written and validated; not applied.)
- [x] Containerize the API, migrator, and workers; publish images to Artifact Registry. (`Dockerfile`; publishing is the deploy workflow's first step, not run yet.)
- [x] Deploy with Kubernetes manifests (Kustomize overlays per environment); run migrations as a pre-deploy Job. (`infrastructure/k8s`; not run against a cluster yet.)
- [ ] Use Workload Identity for all pod access to GCP; no service-account key files. (Configured; verify on first deploy.)
- [ ] Restrict the quarantine bucket to the redaction workload only. (Configured; verify on first deploy.)
- [ ] Put the API behind Gateway API with managed TLS and Cloud Armor (WAF, bot and rate limiting). (Configured; verify on first deploy. Forwarded headers done and tested.)
- [ ] Plan for on-sale traffic spikes: autoscaling, pre-scaling before scheduled on-sales, and load tests. (HPA and procedure written; no load test yet.)
- [ ] Set up CI/CD, logging, monitoring, alerting, backups with point-in-time recovery, and a tested restore. (Workflow, uptime alert, and PITR configured; restore not rehearsed.)

Details: [ADR 0002](docs/adr/0002-hosting-gcp-gke.md) · [Infrastructure](infrastructure/TODO.md)

## Phase 13 — UI testing and launch
- [ ] Mobile component tests (Jest + React Native Testing Library) and end-to-end flows (Maestro) in CI.
- [ ] Promoter dashboard end-to-end tests (Playwright) and accessibility checks.
- [ ] API contract tests against `openapi.yaml`; load tests for on-sale registration spikes.
- [x] Dev environment with synthetic data: `tools/dev/seed.py` (fans, artists, shows, allocations, registrations),
      verified 2026-10-24 against a scratch database and run by CI. The cloud proving ground is **beta** with real
      testers, not a synthetic staging (ADR 0002, amended 2026-10-24); see `infrastructure/README.md`.
      *Requested 2026-10-25: seed real artists and albums (real Spotify ids and covers) instead of made-up ones, so the
      dev app looks right; see `apps/mobile/TODO.md`.*
- [ ] Test accounts for every external provider (Spotify, Apple, ticketers) usable on beta.
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

## Phase 15 — User stats
A fan's numbers in one list: on their own profile in full, and on other fans' profiles subject to visibility
(followers see top fandom stats, decided 2026-10-20). Read from stored state and the ledger, never from Spotify
at request time.
- [ ] Define the stat list and its sources: per tracked artist (fandom score, percentile, rank among fans, estimated
      flag; Phase 5), journal totals (albums logged, relistens, reviews, lists, comments; Phase 6), proof totals
      (verified tickets, merch, screenshots; Phase 7), listening totals for tracked artists only (liked tracks,
      playlist tracks, distinct albums, recent plays; Phase 4), points total from the ledger, followers and
      following (Phase 8).
- [ ] `GET /me/stats` and `GET /users/{userId}/stats` in `openapi.yaml`; *`GET /me/stats` done 2026-10-21 (StatsApiTests 3/3);
      other fans' stats wait on profile visibility; since 2026-10-25 followers get a fan's top scores, albums logged,
      verified photos/merch, and points as `stat` cards on the home feed.* counts exclude soft-deleted content and
      reversed awards.
- [ ] Visibility: own stats in full; other fans' stats follow their profile visibility, followers-only stats for
      followers, nothing for blocked fans.
- [ ] Stats screen in the fan app (own profile and other fans' profiles), with estimated scores labelled.
      *Own stats screen done 2026-10-21 (`/stats`).*
- [ ] Tests for counts after reversals and deletions, visibility, and blocked fans.

Details: [Stats API](services/api/src/features/stats/TODO.md) · [Stats UI](apps/mobile/src/features/stats/TODO.md)

## Cross-cutting requirements
- [ ] Keep all score awards idempotent and ledger-based.
- [ ] Do not store untracked-artist listening data.
- [ ] Do not archive unredacted submission files.
- [ ] Keep secrets out of source control and logs.
- [ ] Enforce authorization and visibility at the API boundary.
- [ ] Add tests for failure cases, concurrency, and duplicate requests.
- [ ] Document decisions that remain proposed or open in the source README.

## Open decisions
- [x] Confirm backend framework and job-queue choice. ASP.NET Core ([ADR 0001](docs/adr/0001-backend-csharp-dotnet.md)); small Postgres queue ([ADR 0004](docs/adr/0004-postgres-job-queue.md)).
- [x] Confirm album catalog provider and its caching/artwork terms. Spotify (app token), minimal metadata cache refreshed weekly,
      removed releases delisted not deleted, cover art hotlinked and unaltered, Spotify logo and link on catalog screens
      ([ADR 0006](docs/adr/0006-album-catalog.md), decided 2026-10-21). MusicBrainz, Apple Music, and Discogs rejected there.
- [ ] Confirm ticketing partner and purchase-confirmation capability.
- [ ] Confirm archive meaning and user-facing archive requirements.
- [ ] Decide how much ledger points (journal, proof of fandom) weigh against the 0–100 Spotify score. For now
      they are added on top. *Since 2026-10-24 the Spotify score is spread over 18 signals (each worth at most
      100/18 points), so a few awards can outrank a strong listener; see `workers/scoring/README.md`.*
- [ ] Decide whether to add live "listening now" check-ins (scope `user-read-currently-playing`; release-day or
      listening-party windows) and a Spotify data-export upload (full streaming history as proof of fandom) as
      further signals. Both wait on events and on the submissions upload flow (reviewed 2026-10-24).
- [ ] Confirm verification thresholds and baseline blend parameters. *Provisional defaults in code since 2026-10-21*
      (`Baseline__MedianScore` 15 on the 0–100 score scale since migration 0008, `Baseline__Sigma` 1, `Baseline__BlendK` 200; stored baselines carry `provisional: true`).
- [x] Define data retention and account deletion behavior. Deleted accounts: personal data scrubbed at once, content hard-deleted after 30 days, comments others replied to kept as "[deleted]", ledger kept anonymously (decided 2026-10-18). File retention (quarantine/archive) still to define with submissions.
- [x] Artist add limit: 50 new artists per user per UTC day (decided 2026-10-18).
- [x] Account linking: no. One email per account; a second provider with the same email gets `409 email_in_use`. (Decided 2026-10-18.)
- [ ] Confirm exact Spotify signals available under current API access. Checked 2026-10-18 for development mode
      (ADR 0005): saved tracks, top artists, last 50 plays, own/collaborative playlists. Track popularity is gone.
- [x] Untracked artists found in a fan's Spotify top artists are tracked automatically for that fan (same path and
      daily limit as adding by hand), never stored as untracked listening data (decided 2026-10-22, ADR 0005).
- [ ] Decide how Spotify signals credit co-credited tracks. The sync counts the primary artist only for now
      (`SpotifySignals.Reduce`).
- [ ] Decide how to handle co-credited albums and collaborations.
- [ ] Confirm the product and company name (summary proposes TrueFan Access or VerifyTicket; repo uses Fandom Score).
- [ ] Decide which user stats are public by default and which are followers-only (Phase 15).
- [ ] Choose the primary ticketing model: white-labeled checkout through ticketer APIs, or direct allocation links.
- [ ] Confirm which primary ticketers will grant API access for reserved inventory, and on what terms.
- [ ] Confirm Spotify's developer terms allow using listening data to decide ticket eligibility, and that the
      app qualifies for extended API quota. Same check for Apple Music. As of 2026-10-18 extended quota is
      organizations only with 250k monthly active users; development mode allows 5 users (ADR 0005).
- [ ] Check laws on non-transferable tickets and ticket resale in each launch market (some US states restrict non-transferable tickets).
- [x] Selection method for oversubscribed allocations (decided 2026-10-20): **ranked by fandom score**, among
      fans who have the show's city in one of their **three hub cities**.
- [x] Allocation edge cases (decided 2026-10-20): seats hub fans leave unfilled **open to fans outside the
      city**; **equal scores are broken by a coin flip** (a random draw, recorded so the result can be audited).
- [x] Allocation rules (decided 2026-10-24): a selected fan may bring a **+1**; an allocation's **rules lock once
      registration opens** (it can still be cancelled); selection ranks fans by the score snapshot taken **when
      they registered** (frozen, can't be pumped afterwards). Staff, not promoters, manage shows until
      organizations exist.
- [x] Eligibility threshold (decided 2026-10-24): a **low raw-score minimum, equal to having 2 saved albums** by the
      artist (`min_raw_score`, set from `album_saves_score()` when an allocation is published, unless staff set one).
- [x] +1 (decided 2026-10-24): the guest **takes a seat from the allocation**; the guest **needs an account, which can be
      made later**; the +1 can be asked for **at registration or added after being selected**.
- [ ] +1 rules set provisionally in code (2026-10-24), confirm or change: when only one seat is left for a fan with a
      +1, they **get the seat alone**; a named guest **can't be changed**; a selected fan **can't drop** their +1 after the
      draw; a +1 can be added after the draw **until the show starts**, first come first served while seats are left.
- [ ] What pausing an artist (takedown) does to an open allocation (for now it's allowed, and registration and
      selection will refuse).
- [x] Concert-photo points: `concert_photo_verified` is 5, the same as a screenshot (decided 2026-10-21).
- [x] Sharing rules (decided 2026-10-21): only verified merch and concert photos can be shared; tickets and screenshots
      never (barcodes, personal details). The feed shows everyone's shared photos and public album logs.
- [x] Concert photos are pinned to the band and the concert (decided 2026-10-21): a listed show, or date, venue, and
      city entered by the fan when the show isn't listed.
- [ ] Confirm takedown effects (provisional, 2026-10-24): a taken-down photo leaves the feed for good but stays as
      proof with its points; a taken-down log is hidden from everyone but its author and loses its points.
- [ ] Choose an email provider for staff notifications (and later fan email), e.g. a transactional email API.
- [ ] Decide on the fan-side fee and whether App Store / Google Play payment rules apply to it.
- [ ] Choose the first pilot artist(s) and promoter.
- [x] Concert data source (decided 2026-10-20): **concert APIs** for shows and sale dates (candidates:
      Ticketmaster Discovery, SeatGeek, Bandsintown, Songkick), plus possibly a **scraper for news and presale
      announcements**, including artists' X/Twitter and Instagram posts.
- [ ] Choose the concert APIs, and the presale/news sources a scraper may use. X and Instagram forbid
      scraping in their terms, and their official APIs are paid or restricted, so check each source's terms
      first. Prefer artist/label newsletters, official sites, and RSS where possible.
- [x] A "concert hub" is a **city** (decided 2026-10-20): each fan picks a primary city plus two backups.
- [x] Hubs affect allocations: **local fans first** (decided 2026-10-20), via the selection method above.
- [x] A hub city is **required at sign-up** (decided 2026-10-20).
- [x] Hub cities come from a **curated list of US and Canadian metro areas** (decided 2026-10-21), not a
      geocoding service. Staff add rows to `cities` to cover more; other countries are more rows.
- [x] Hub cities can be changed **once a week**, with a **warning while changing** (decided 2026-10-20).
- [x] A hub change after registering for an allocation **doesn't affect that registration** (decided 2026-10-20).
- [x] Social model (decided 2026-10-20): **follow/follower**, not mutual friends.
- [x] Follow rules (decided 2026-10-20): following needs **no approval**; a fan can **remove** a follower and/or
      **block** someone. Followers see the fan's **posts** and **top fandom stats**.
- [x] Bouncer door song (decided 2026-10-20): a **recommended real track** (not generated audio), based on the
      taste of someone the fan **follows**.
- [x] Grab bag (decided 2026-10-20): each fan can place **5 favorite songs** in a grab bag. The bouncer door
      draws a song from the grab bags of people the fan follows. Grab bags are **shared with followers by
      default** (changed from opt-in the same day), and the owner's **name is shown** with the song.
      Each song **links to Spotify**. Adding a song by an untracked artist **starts tracking that artist**.
- [x] Grab-bag sharing **can't be turned off**, and artists tracked through a grab bag **don't count** toward the
      50-artists-per-day limit (decided 2026-10-20). `track_artist()` counts rows by `added_by`, so grab-bag adds
      need to be recorded separately.
