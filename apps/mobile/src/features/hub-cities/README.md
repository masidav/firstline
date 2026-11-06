# Hub cities mobile UI

## Purpose
Choosing a primary hub city and up to two backups (`PUT /me/hub-cities`), which decide where the fan is first in
line, and the optional location checks that confirm the choice (`POST /me/location-checks`).

## Files
| File | Role |
| --- | --- |
| `HubCitiesScreen.tsx` | Search and pick cities; weekly change limit; after a save, the optional "Confirm you're near …?" step. |
| `locationCheck.ts` | `recordLocationCheck` (permission prompt, one reading, the POST) and `hasLocationPermission`; both quiet, never throw. |

## Location checks
Sharing location is the fan's choice and never blocks anything. Three moments, as `openapi.yaml` lists them:
- `hub_set`: right after saving hub cities, the screen offers "Share my location" / "Not now". Sharing asks for the
  system permission (`expo-location`, foreground only; the browser's geolocation on the web) and shows one line:
  confirmed near the hub, nearest to another city (the hub stays), or couldn't read the location.
- `eligibility` and `registration`: the show screen (`src/features/ticketing/ShowScreen.tsx`) runs them in the
  background, only for fans who already allowed location, once when an open allocation's standing loads and right
  after a registration is created (201). It never prompts and never waits for the result.

The position is sent once and not stored; the API keeps the nearest city, rounded distances, and whether a hub was
within 100 km. Permission strings are in `app.json` (`expo-location` plugin, when-in-use only, no background location).
