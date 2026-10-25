# Grab-bag worker jobs

| File | Job | What |
| --- | --- | --- |
| `GrabBagRefreshJob.cs` | `grab_bag.refresh_songs` (daily) | Re-reads from Spotify (app token) the grab-bag songs not refreshed for 7 days, 200 per run oldest first, and updates their title, album, and cover (migration 0019). A track Spotify no longer has is stamped and left as it was. Does nothing while `Spotify__ClientSecret` is unset. |

Grab bags themselves are `services/api/src/features/grab-bag`. Tests: `tests/FandomScore.Tests/GrabBagRefreshJobTests.cs`.
