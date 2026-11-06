# Hub location checks: confirming a fan is where their hub cities say

Design note, 2026-11-05 (Phase 10, "detect streaming-farm and bot behavior"; requested 2026-11-05: "use location tied
to the hub to confirm not bots"). Implemented by migration `0057_hub_location_checks.sql`, `POST /me/location-checks`
(`services/api/src/features/fraud/LocationChecks.cs`), and the fan app's location prompts. The rules run in the
existing `fraud.detect` sweep.

## Why
Fans whose hub cities include a show's city go first in the draw (migrations 0010, 0013). That makes a hub city worth
lying about: a reseller in another state picks Chicago as a hub and is "local" to every Chicago show, and a farm of
accounts can all claim the same city at no cost. Until now nothing checked a hub claim against anything. A device
location, shared by a fan who wants to, is the cheapest thing that does.

## Principles (same as `bot-and-shared-device-detection.md`)
- **Optional, and never a penalty.** The app asks; a fan can say no, and nothing lowers their score or standing for
  it. What a match does is positive: it confirms the hub for the trust score (a tie-breaker only). What a mismatch
  does is ask staff to look, like every other rule.
- **No coordinates are stored.** The position reaches one database function (`record_hub_location`) as bound
  parameters, which Postgres doesn't log, and leaves it as: the nearest curated city, the distance to it and to the
  fan's nearest hub city rounded to 10 km, and whether a hub city was within 100 km. There is no column for a
  position, and the invariants test checks that.
- **Only at three moments, never in the background:** setting hub cities (`hub_set`), opening a show's fan-ticket
  standing (`eligibility`), registering (`registration`). No background location, no geofencing, no trail of where a
  fan goes. A reading with accuracy worse than 50 km (an IP-level guess, a desktop browser) is dropped.
- **Short-lived, deleted with the account, counts only in evidence.** Rows live 90 days (`prune_client_signals`),
  go at once when the account is deleted (`scrub_deleted_user_signals`), and the evidence a rule writes holds
  counts, days, and rounded distances, never a city name tied to a time. Staff see "2 checks on 2 days, nearest hub
  2 800 km away".

## What is stored
| Column | What |
|---|---|
| `kind` | `hub_set`, `eligibility`, `registration` |
| `allocation_id` | the show, for the two show kinds |
| `nearest_city_id`, `nearest_city_km` | the nearest curated city (`cities`, now with coordinates) and the distance to it, rounded to 10 km |
| `nearest_hub_km` | distance to the fan's nearest hub city at the time, rounded to 10 km; null without hub cities |
| `hub_match` | a hub city was within 100 km (`hub_radius_km`; metro areas are big, and Riverside to Los Angeles is 90 km) |
| `at` | when |

## What it feeds
- **Trust** (`fan_trust_parts`, 0039): a fifth part, `hub_confirmed`, 1 when a check in the last 180 days matched a
  hub city. Equal weight with account age, verified proof, steady listening, and good standing. The trust score only
  breaks ties between equal raw scores in a draw (decided 2026-10-28), so a fan who never shares their location loses
  nothing but a tie-break against a fan who did.
- **Rule `hub_location_mismatch`** (reason `other`): 2 or more checks on distinct days in the last 90 days, each at
  least 500 km from every hub city the fan had then. The fan is claiming a hub they are never near.
  False positives: a fan who moved and hasn't changed hubs yet (they can change weekly); a fan away for a month
  registering for shows back home. Staff review it; the fan isn't told until they uphold it (0044).
- **Rule `impossible_travel`** (reason `duplicate_account`): two checks within 6 hours whose nearest cities are
  1 500 km or more apart (the distance is between the cities; the fan's own position is never stored). One account
  used from two places at once: shared among friends (strategy 6 in `gaming-strategies.md`), or a cluster of people
  behind one sign-in. False positives: a VPN exit far from the phone (only if the browser reports the VPN's location,
  which GPS-backed readings don't); a fan on a flight (6 hours is under a transcontinental flight).
- The sweep also checks every fan with a location check in the last day.

Thresholds are data in `fraud_rules` and provisional (listed under "Fraud detection thresholds set provisionally" in
`PROJECT_TODO.md`).

## What it can't do
- A bot that never shares its location is never seen here. That's by design: sharing is optional. The value is on
  the positive side (confirmed fans) and against the lazy case (one person, one phone, many accounts, all checking
  in from the same far-away place: `shared_device` and this rule together).
- A spoofed location (a developer-mode fake GPS) looks real. Combined with `shared_device`, `shared_address_*`, and
  `impossible_travel` (a spoofer who forgets to move the fake between accounts), it still costs effort per account.
- Nothing confirms the fan was *at the show*; attendance verification is a separate open item (Phase 10, "verified
  attendance history") and would want a check-in at the venue on the night, which is a different product decision.

## Owner decisions this raises
1. Confirm the thresholds: 100 km hub radius; mismatch at 2 days and 500 km over 90 days; travel at 6 hours and
   1 500 km over 30 days.
2. Retention 90 days and the tables staying out of the fan's data download (as for the 0054 signals). The
   alternative is listing the checks in `GET /me/export` under "tickets": they are coarse (city and rounded km) and
   the fan chose to share them, so showing them back is defensible either way.
3. Whether `hub_confirmed` should weigh like the other four trust parts, or less, until real data shows how many
   fans share their location.
