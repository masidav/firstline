# Promoter dashboard (web)

Where promoter organizations run their fan allocations: create a show and its allocation, publish it, watch fans
register, run the draw, and export the results. React + TypeScript (Vite); the API client is generated from
`openapi.yaml` like the fan app's. Promoters sign in with the same accounts fans use, and see fans as handles and
registration status only (`services/api/src/features/organizations/README.md`).

## Run it
1. The API in Development with developer sign-in, this origin allowed, and the worker for draws (repo root):
   ```bash
   Auth__DevSignIn=true Cors__AllowedOrigins=http://localhost:5173 dotnet run --project services/api
   dotnet run --project workers      # runs queued draws (allocations.select)
   ```
2. An organization with you as owner: sign up once (here or in the fan app) to get a handle, then as staff
   `POST /v1/admin/organizations {"name": "...", "owner_handle": "you"}` (the seed's `dev_staff` is staff).
3. The dashboard:
   ```bash
   cd apps/promoter-web
   cp .env.example .env.local    # API URL, developer sign-in, Google client ID
   npm install
   npm run dev                   # http://localhost:5173
   ```
For Google sign-in, set `VITE_GOOGLE_WEB_CLIENT_ID` (the same web client as the fan app works) and register
`http://localhost:5173/google-callback` as an authorized redirect URI.

## What it does
| Page | Path | Who |
| --- | --- | --- |
| Your organizations (straight to it when there is one) | `/` | Anyone signed in |
| Shows, with each allocation's state | `/o/{org}` | Every member |
| New show: artist, hub city, venue, time in the city's time zone | `/o/{org}/shows/new` | Admins, owners |
| A show: edit it; add, publish, cancel, or delete allocations | `/o/{org}/shows/{show}` | Admins, owners change; analysts read |
| An allocation: registration totals and list (refreshes every 10 s while open), the draw (queued, then followed every 3 s until done), tickets held, CSV export | `/o/{org}/shows/{show}/allocations/{allocation}` | Admins, owners run the draw; analysts read |
| Members: add by handle, change roles, remove | `/o/{org}/members` | Owners change; others read |

Times are entered and shown in the show city's time zone (`src/lib/time.ts`), whoever is looking, and sent to the
API as UTC.

## Layout
| Path | Contents |
| --- | --- |
| `src/api/` | `schema.d.ts` (generated, don't edit), `client.ts` (typed client, `ApiError`, messages). |
| `src/auth/` | `SessionManager` (same rules as the fan app's), `SessionProvider`, Google sign-in. |
| `src/pages/` | One file per page, plus `AllocationForm`. |
| `src/lib/` | Time zones, money and CSV, artist names, `useLoad`. |
| `src/ui/` | Shared components and the search picker. |
| `e2e/` | Playwright journeys and the axe accessibility scan against the real API (`e2e/run.sh`). |

## Commands
```bash
npm run dev            # dev server
npm test               # Vitest component tests
npm run typecheck
npm run generate:api   # after openapi.yaml changes (CI checks it)
npm run build          # static site in dist/ (any static host; serve index.html for every path)
```

## Hosting
Beta hosts it at `https://promoters.beta.firstline.com`: the `Dockerfile` target `promoter-web` builds it and serves it
with nginx (`infrastructure/README.md` "Web apps"). Settings come at startup from `/config.js`, which nginx writes
from its environment, so the same image serves beta and prod; locally the `VITE_` values apply (`src/config.ts`).
Google sign-in needs `https://promoters.beta.firstline.com` as an authorized origin of the web client and
`https://promoters.beta.firstline.com/google-callback` as a redirect URI.

```bash
docker build --target promoter-web -t promoter-web .
docker run --rm -p 8082:8080 -e API_URL=http://localhost:5271/v1 -e DEV_SIGN_IN=true promoter-web
e2e/run.sh             # journeys: throwaway database, migrations, seed, API, a production build, Chrome
```

## Tests
- `src/**/*.test.ts(x)` (Vitest + Testing Library): time zones across daylight-saving changes, CSV, the shows list, the
  draw button only for admins, CSV export, member management only for owners. `src/accessibility.test.tsx` runs axe
  (vitest-axe) on states the browser scan doesn't reach: validation errors, a failed draw, the server unreachable.
- `e2e/promoter.spec.ts` (Playwright, in CI): a promoter creates a show and allocation, two fans register, the draw
  runs, results and export check out; an analyst can't change anything and another organization stays hidden; Google
  sign-in for a new promoter. The worker's draw is stood in for by the same database function.
- `e2e/accessibility.spec.ts` (Playwright + `@axe-core/playwright`, in CI): every page the journeys visit, in the states
  they reach (forms open, search results showing, registrations listed, the draw done with tickets held), scanned
  against WCAG 2.0–2.2 A/AA and axe's best practices. Serious and critical violations fail; lesser ones print.

## Not yet
An eligible-fan count before opening registration, reports beyond the CSV, and fan data sharing with consent.
Beta hosting is configured but not live until its DNS records and certificate exist and deploys are turned on.
