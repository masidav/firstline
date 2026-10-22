# Artist onboarding worker

Takes a newly tracked artist from `onboarding` to `active`, and keeps active artists' catalogs current.
Design: [ADR 0006](../../docs/adr/0006-album-catalog.md) (catalog), [ADR 0004](../../docs/adr/0004-postgres-job-queue.md) (queue).
Schema: `database/migrations/0007_artist_catalog.sql`.

| File | Job kind | What |
| --- | --- | --- |
| `ArtistOnboardingJob.cs` | `artist.onboard` | Queued by `track_artist()` (through `request_artist_onboarding()`) in the same transaction as the new artist. Loads the catalog, stores a provisional baseline, sets the artist `active`, and queues a Spotify sync for the fan who added it. Other fans pick the artist up on their next scheduled sync. |
| `CatalogRefreshJob.cs` | `catalog.refresh` | Reloads one active artist's catalog. `ScheduleCatalogRefreshesJob` (`catalog.schedule_refreshes`, hourly) queues it for artists whose catalog is older than `Spotify__CatalogRefreshEvery`, 200 per hour, oldest first. |
| `CatalogLoader.cs` | | Shared load: `GET /artists/{id}/albums` (albums, singles, compilations; one market) and `GET /albums/{id}/tracks`, upserted by Spotify id in one transaction. Releases Spotify no longer lists are **delisted** (`delisted_at`), never deleted, so fans' logs survive. Only releases whose first-credited artist is the tracked artist are kept. |
| `ProvisionalBaseline.cs` | | The lognormal prior stored in `artist_baselines` at activation, from the `Baseline` settings below. Provisional until the baseline parameters are decided (open decision in `PROJECT_TODO.md`). |

## Behaviour
- **Idempotent.** An artist that is no longer `onboarding` is skipped; a repeated load gives the same rows.
  Onboarding and refresh of one artist never run together (advisory lock `catalog:<artist id>`).
- **Failures.** Spotify outages and rate limits throw, so the queue retries with backoff (up to 10 attempts);
  the artist stays `onboarding` and nothing else changes. After the last attempt the job is `failed` with
  `last_error` set; retry it with `SELECT request_artist_onboarding('<artist id>')` or the SQL in `workers/README.md`.
- **Removed artists.** If Spotify has no artist with the id (404), the artist is set to `paused`. Staff can set it
  back to `onboarding` and queue it again.
- **Not configured.** Without `Spotify__ClientId` and `Spotify__ClientSecret` the job throws and retries, so
  artists added before the server is configured onboard once it is.
- **Activation guard.** `trg_artist_activate` refuses `active` without a baseline row and `catalog_loaded_at`.

## Configuration (all optional)
| Setting | Default | Meaning |
| --- | --- | --- |
| `Spotify__CatalogMarket` | `US` | Country whose releases make up the catalog (ADR 0006). |
| `Spotify__CatalogRefreshEvery` | `7.00:00:00` | Reload interval per active artist. |
| `Spotify__MaxCatalogAlbums` | `500` | Releases loaded per artist, in Spotify's order. |
| `Baseline__MedianScore` | `5` | Raw score of the median fan under the provisional prior. |
| `Baseline__Sigma` | `1` | Spread of log(score). |
| `Baseline__BlendK` | `200` | Real fans needed before real data carries half the weight. |

## Tests
`tests/FandomScore.Tests/ArtistOnboardingJobTests.cs` (Spotify faked by `FakeSpotify`): onboarding through the
queue, skipped when already active, outage retry, missing client secret, unknown artist paused, album cap,
refresh delisting and restoring, refresh pausing a removed artist, the refresh schedule, release-date precision,
and the baseline curve. `database/tests/invariants.sql` covers the job enqueue, the activation guard, and the
column constraints.

## Known limits
- Spotify no longer returns `popularity` or followers (February 2026), so `tracks.popularity` stays null and the
  baseline's only proxy metrics are the catalog size.
- Collaboration albums go to whichever artist Spotify credits first; co-credits are an open decision.
- Deluxe editions and re-releases are separate Spotify albums and stay separate here.
