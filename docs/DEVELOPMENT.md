# Development

Three environments: **dev** is your machine (everything on `127.0.0.1`, fake data, this document), **beta** is the
GCP environment real beta testers use (every push to `main` deploys there), **prod** is promoted from beta after
approval. The cloud side is in [infrastructure/README.md](../infrastructure/README.md).

## Prerequisites
- .NET SDK 10.0 (see `global.json`). Without a local SDK, use the `mcr.microsoft.com/dotnet/sdk:10.0` container.
- Docker, for the local database and for the test suite (Testcontainers).

## Layout
| Path | Project |
| --- | --- |
| `FandomScore.slnx` | Solution |
| `services/api` | `FandomScore.Api`: ASP.NET Core API. Feature code goes in `src/features/<feature>`. |
| `workers` | `FandomScore.Worker`: background jobs from the Postgres queue ([README](../workers/README.md)). |
| `packages/spotify` | `FandomScore.Spotify`: Spotify client and token encryption, shared by the API and worker. |
| `database` | `FandomScore.Database`: migration runner. SQL migrations in `database/migrations`. |
| `database/tests/invariants.sql` | Database invariant tests (run by the test suite). |
| `tests/FandomScore.Tests` | xUnit tests: migrations, invariants, API. |
| `infrastructure/docker-compose.yml` | Local PostgreSQL 16. |
| `infrastructure/` | Cloud deployment: Terraform, Kubernetes manifests, deploy script ([README](../infrastructure/README.md)). `Dockerfile` at the root builds the images. |

## First run
```bash
cp .env.example .env                       # set a local password in both places, and
                                           # Auth__SigningKey=$(openssl rand -base64 32)
docker compose --env-file .env -f infrastructure/docker-compose.yml up -d
set -a; . ./.env; set +a                   # export ConnectionStrings__Database (keep it quoted: it contains ;)
dotnet run --project database              # apply migrations
dotnet run --project services/api          # http://localhost:5271/v1/health
dotnet run --project workers               # background jobs (separate terminal)
```

## Seed data
`tools/dev/seed.py` fills the local database with a realistic, deterministic data set: 200 fans (hub cities,
Spotify-like signals, album journal logs that earn ledger points through the real triggers, a few verified tickets
and merch with the merch shared to the feed, follows between fans, and grab bags), 8 active artists with albums and tracks, 10 shows with a fan allocation each in a different state (open,
upcoming, high threshold, closed with the selection draw run, draft, cancelled), registrations made through
`register_for_allocation()` (some with a +1), plus the staff account `dev_staff`.
```bash
python3 tools/dev/seed.py                  # on an empty database (after the migrations above)
python3 tools/dev/seed.py --wipe           # wipe the local database first (asks), then seed
python3 tools/dev/seed.py --fans 2000 --seed 7
```
It refuses to run on a database that already has seed data. Sign in from the app with dev sign-in as `dev_staff`
or any fan handle it prints (`Auth__DevSignIn=true`). Scores are provisional until the worker runs the queued
rescores. Nobody has a Spotify connection; connect your own as usual. The artists are fake (`seed:` ids), so after a
week the weekly catalog refresh logs a failed job per seed artist; re-seed or ignore. Local only: never run it
against beta or prod (`infrastructure/README.md`).

## Fan app
The app in `apps/mobile` runs in the browser for now; see its [README](../apps/mobile/README.md), including how to
open it on your phone. Set `Auth__DevSignIn=true` and `Cors__AllowedOrigins` in `.env` to sign in with test names.

## Tests
```bash
dotnet test FandomScore.slnx
```
The suite starts its own PostgreSQL 16 container, so it needs Docker but not the compose database.

## Migrations
- Add a new file `database/migrations/NNNN_description.sql` with the next number. DbUp runs each file once,
  in name order, in its own transaction, and records it in the `schemaversions` table.
- Never edit a migration that has already been applied anywhere.
- Add invariant checks to `database/tests/invariants.sql` for new triggers and constraints.

## Configuration
- Secrets come from environment variables only (`ConnectionStrings__Database`, `Auth__SigningKey`);
  `appsettings*.json` holds no secrets. The API refuses to start without a database connection string and a
  signing key of at least 32 bytes.
- Sign-in needs real Apple/Google client IDs (`Auth__Apple__ClientIds`, `Auth__Google__ClientIds`); without them
  `POST /v1/auth/sessions` returns `422` for that provider. The test suite replaces the provider check with a fake.
  See [ADR 0003](adr/0003-authentication.md).
- Spotify needs `Spotify__ClientId`, `Spotify__RedirectUris`, and `Spotify__TokenKey` (`openssl rand -base64 32`), the
  same values for the API and the worker. Without a client ID, Spotify is off and connecting returns `503`. In the
  Spotify dashboard, register the redirect URIs and add each tester's Spotify account under Users (development
  mode allows 5). Artist search and add also need `Spotify__ClientSecret` (the dashboard's client secret);
  without it they return `503`, and the worker cannot load catalogs (added artists stay `onboarding` until it is set).
  `Spotify__TokenKey` is a key you generate, not anything from Spotify. See [ADR 0005](adr/0005-spotify-integration.md);
  catalog settings (`Spotify__CatalogMarket` and friends) are in `workers/artist-onboarding/README.md`.
- To connect a real Spotify account without the mobile app: `python3 tools/dev/spotify_connect.py --help`. It
  creates a local dev user and session (local database only), runs Spotify's sign-in in your browser, calls
  `PUT /v1/me/spotify`, and shows what the sync stored. Register `http://127.0.0.1:8888/callback` in the
  Spotify dashboard and add it to `Spotify__RedirectUris`.
- `testScripts/` holds local helpers, runnable from any directory: `startWebAppLocal.sh` (fan app in the
  browser), `testSpotifyAPI.sh` (add a test artist and connect Spotify), and `wipeLocalDb.sh` (deletes ALL data
  in the local compose database and re-applies migrations; asks first unless given `--yes`), and
  `scoringWeights.sh` (lists the Spotify scoring weights, or sets one: `scoringWeights.sh spotify_liked_track 2`;
  the worker rescores every artist afterwards).
- Uploaded photos are stored on local disk under `services/api/.local-storage/` (`Storage__LocalPath` to change it;
  git ignores it). Production needs the Cloud Storage quarantine bucket, which isn't wired up yet. To review posts
  locally, make your account staff: `UPDATE users SET is_staff = true WHERE handle = '...'` in the compose database.
- Production logs are JSON on the console. Development uses the plain formatter.
- In Kubernetes, secrets arrive as files in `Secrets__Directory` (one per configuration key) instead of environment
  variables, and `ForwardedHeaders__KnownProxies`/`KnownNetworks` tell the API which load balancer to trust for
  client addresses. Neither is needed locally.
