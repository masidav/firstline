# Development

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

## First run
```bash
cp .env.example .env                       # set a local password in both places, and
                                           # Auth__SigningKey=$(openssl rand -base64 32)
docker compose --env-file .env -f infrastructure/docker-compose.yml up -d
set -a; . ./.env; set +a                   # export ConnectionStrings__Database
dotnet run --project database              # apply migrations
dotnet run --project services/api          # http://localhost:5271/v1/health
dotnet run --project workers               # background jobs (separate terminal)
```

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
  mode allows 5). See [ADR 0005](adr/0005-spotify-integration.md).
- To connect a real Spotify account without the mobile app: `python3 tools/dev/spotify_connect.py --help`. It
  creates a local dev user and session (local database only), runs Spotify's sign-in in your browser, calls
  `PUT /v1/me/spotify`, and shows what the sync stored. Register `http://127.0.0.1:8888/callback` in the
  Spotify dashboard and add it to `Spotify__RedirectUris`.
- Production logs are JSON on the console. Development uses the plain formatter.
