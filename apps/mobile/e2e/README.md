# End-to-end journeys (browser)

The fan app's critical journeys in a real browser, against the real API and a real, seeded database. Beta deploys
from `main`, so these run in CI on every push (`.github/workflows/ci.yml`, job "End-to-end journeys").

| File | Journey |
| --- | --- |
| `signup.spec.ts` | Sign up (developer sign-in, handle, hub city, home); reload a deep page and stay on it; a signed-out deep link ends at sign-in. |
| `google.spec.ts` | Continue with Google: what the app sends to Google (Google itself is stubbed), coming back with a token, a new account; an answer that didn't start in this browser is refused. |
| `journal.spec.ts` | Log an album with a rating and review and earn points; log it offline, then see it sync once (one log on the server). |
| `allocation.spec.ts` | Staff open a fan allocation (admin API); an eligible fan registers, stays registered after a reload, withdraws. |
| `account.spec.ts` | Sign out and back in to the same account; delete the account and get a new one on the next sign-in. |

## Run it
```bash
cd apps/mobile
e2e/run.sh                          # everything: throwaway database, migrations, seed, then the suite
e2e/run.sh journal.spec.ts --headed  # one file, with the browser visible
```
`run.sh` needs Docker, the .NET SDK, Node, Python 3, and Chrome (or `npx playwright install chromium`, then
`E2E_CHANNEL= e2e/run.sh`). Against a database you prepared yourself: `E2E_DATABASE_URL=postgres://... npm run e2e`.

Playwright starts two servers itself (`playwright.config.ts`): the API with `dotnet run` (Development, developer
sign-in on, a high sign-in rate limit), and the production web export (`expo export --clear`) served like a static
host by `serve.mjs`. `--clear` matters: Metro's cache can keep a module built with other `EXPO_PUBLIC_` values from a
dev-server run and put them in the build.

## How the tests are written
- Each test signs up its own accounts with unique names (`uniqueName`), so tests run in parallel and the database
  can be reused.
- Setup goes through the app or the API where it can (staff create shows through `/v1/admin`). What only a background
  job would do, like computing a fandom score after a Spotify sync, is written to the database directly (`sql`).
- Seed data (`tools/dev/seed.py --fake`) provides active artists with albums.
- On failure, Playwright keeps a screenshot and a trace in `e2e/test-results/` (CI uploads them as `e2e-report`):
  `npx playwright show-trace e2e/test-results/<test>/trace.zip`.

Not covered here: the native iOS and Android apps (Maestro, `docs/plans/ui-testing-and-launch.md`), real Google and
Spotify sign-in, and staff screens.
