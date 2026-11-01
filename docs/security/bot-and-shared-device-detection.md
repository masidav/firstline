# Bot and shared-device detection: signals, rules, and the privacy trade-off

Design note, 2026-11-01 (Phase 10, "detect streaming-farm and bot behavior"). Implemented by migration
`0054_client_signals.sql`; the rules run in the existing `fraud.detect` sweep (`workers/fraud/DetectFraudJob.cs`).
The play-pattern and "click everything" rules are described in `gaming-strategies.md`; this note covers what was
missing: linked accounts (one person, many accounts) and clients that aren't the app.

## Principles
- **Minimal and proportionate.** No fingerprinting library, no device ids beyond what the app already sends, no
  user-agent storage, no per-request log. Every new row is a count or a salted hash, kept for a fixed short period,
  and goes at once when the account is deleted (`AccountDeletionTests` scans every table).
- **A rule only asks for a review.** As decided 2026-10-29, an open rule flag blocks registering for fan tickets and
  nothing else; the hold that withdraws a fan from a draw needs a person to uphold the flag. Nothing here auto-bans,
  and the fan isn't told until staff uphold the flag (decided 2026-10-30), so the rules can't be probed.
- **Staff see counts and categories, never identifiers.** Evidence is "3 accounts on one device", not which device
  or which accounts; the hashes stay in the database and are never shown. Staff who want to see further use the
  eligibility audit and the fan's profile, as today.
- **Consistent with the privacy posture.** Logs live 30 days (DATA_INVENTORY.md), so no signal here outlives what
  the load balancer already keeps about the same request, except the counts, which carry no address.

## What the API can see today
| The API knows | Where | Kept until now |
|---|---|---|
| The client address of every request (`X-Forwarded-For` from the load balancer, `ForwardedHeadersSetup`) | in memory for the rate limiter; load-balancer logs 30 days | nothing stored |
| The Expo push token of the phone, when the fan allows notifications (`push_devices`) | `push_devices` (unique per token; the row moves to whoever signs in last, PROJECT_TODO 2026-10-28) | the current owner only: the history was lost |
| The Spotify account connected to the fan account (`spotify_connections.spotify_user_id`, unique) | live connection only; a disconnect deletes the row, so the same Spotify account can be connected to another fan account afterwards | nothing after disconnect |
| When the account was created, every registration and refusal, each with its time (`users.created_at`, `eligibility_audit`) | kept | kept |
| Which screens a client loads: in particular `GET /allocations/{id}/eligibility`, which the app always calls before showing the register button | not stored | nothing |
| Sessions (`auth_sessions`): when created and refreshed, no device or address | kept 30 days after expiry | unchanged |

Not available, and deliberately not added: a device fingerprint, the user agent, cookies on the web app, payment
instruments (there are no payments), phone numbers (one-person-per-account verification is a separate open item).

## Signals collected (migration 0054)
| Signal | Stored as | Retention | On account deletion |
|---|---|---|---|
| Client address at **sign-up** and at each **registration** | `client_address_events`: `sha256(daily salt ‖ address)`, the UTC day, the kind (`signup`, `registration`), the allocation for a registration. The daily salt (`signal_salts`, `address:<day>`) is random, generated in the database, and **deleted after 2 days**, after which the hash can't be matched against any address, even by us. Hashes of different days can't be matched to each other by construction. | 30 days | deleted at once |
| **Push device history**: which accounts registered the same push token | `push_device_accounts`: `sha256(token)`, the account, first and last seen. Written by a trigger when `push_devices` gets a new owner. The token itself is already stored (`push_devices`) and is random, so the hash is a pseudonym for the phone, not a fingerprint | 90 days after last seen | deleted at once |
| **Spotify account history**: which accounts connected the same Spotify account | `spotify_account_links`: `sha256(fixed salt ‖ spotify_user_id)`, the account, first and last linked. The fixed salt (`signal_salts`, `spotify_account`) stays, because reuse is only visible across months | 180 days after last linked | deleted at once |
| **Eligibility checks**: how often the fan loads a show's fan-ticket standing | `eligibility_views`: one row per fan, allocation, and UTC day with a count and the first/last time. No request times beyond those two | 90 days | deleted at once |

No raw address is stored anywhere; the address is passed to one database function as a bound parameter (Postgres
doesn't log those) and hashed there. None of these tables is in the fan's data download: the hashes mean nothing
on their own, and the counts are about the service's behaviour towards abuse rather than content the fan created
(the same reasoning as `auth_identities`; listed as such in DATA_INVENTORY.md). The evidence a rule writes into
`fraud_flags.evidence` holds counts, days, and allocation ids only.

## Rules (`fraud_rules`, thresholds provisional)
| Rule | Flags as | Fires when (starting values) | What it catches | Known false positives |
|---|---|---|---|---|
| `shared_device` | shared_device | A push device the fan used in the last 90 days was registered by **3 or more** accounts in that time | one phone, several accounts (a streaming farm or ticket reseller cycling accounts) | a shared family phone; a demo phone at a venue |
| `shared_address_signups` | duplicate_account | **5 or more** accounts created from the fan's sign-up address on the **same UTC day**, in the last 30 days | bulk sign-ups from one machine or network | a dorm, office, or carrier-grade NAT the day a show is announced |
| `shared_address_registrations` | duplicate_account | **5 or more** accounts registered for the **same allocation** from the same address on the same day, in the last 30 days | a cluster going for one show | friends registering together on one Wi-Fi (the rule is per show, so this needs five of them) |
| `spotify_account_reuse` | duplicate_account | The fan's Spotify account was linked to **2 or more other** fan accounts in the last 180 days, or the fan connected **3 or more** different Spotify accounts | one Spotify account passed around to qualify several fan accounts; one person trying accounts | a fan who deleted and re-created their account (2 accounts, under the threshold) |
| `headless_registration` | bot | **2 or more** registrations in 30 days with **no eligibility check** of that allocation before registering (the app always checks first) | scripts calling the API directly | none from the app; a future client must call eligibility first (documented in the API contract) |
| `instant_registration` | bot | **2 or more** registrations within **10 minutes** of creating the account, in the last 30 days | scripted sign-up-and-register; the human flow (connect Spotify, wait for the sync and score, choose hub cities, open two shows) takes longer | a very quick fan the day two shows open; staff review it |
| `eligibility_polling` | bot | **300 or more** eligibility checks of one allocation in one UTC day, in the last 7 days | a script polling the standing (well under the 1 200/min rate limit, so the limiter never sees it) | none expected; a fan refreshing the show page lands in the tens |

The `fraud.detect` sweep (every 6 hours) now also checks fans who signed up, registered, checked eligibility,
registered a push device, or connected Spotify in the last day, and every other account that shares a device,
address-day, or Spotify account with one of them, so a cluster is reviewed as a whole. One flag per fan per sweep
with every rule that fired in `evidence.rules`; the flag is named by the strongest reason (bot, then streaming_farm,
then the rest), a held fan isn't flagged again, and a cleared rule waits its `cooldown_days` (30), as before.

## What still isn't detectable, and why
- **Request cadence that's too regular** (a script hitting endpoints at exact intervals): needs per-request
  timestamps, which the API doesn't keep and this note chose not to add (that's a request log with a user id).
  The load balancer logs have it for 30 days; Cloud Armor or a log-based alert is the owner's call
  (`PROJECT_TODO.md`). The daily eligibility count is the proportionate stand-in.
- **A bot that behaves like the app**: checks eligibility, waits, registers once per show, one account per phone
  and address. Nothing here sees it; the play-pattern rules and one-person-per-account verification are the
  answer to that.
- **Linked accounts that never share a phone, address-day, or Spotify account** (different SIMs, VPNs, bought
  Spotify accounts): not detectable without payment or identity signals.
- **Clusters that rotate addresses daily**: by design the daily salt makes addresses unlinkable across days; a farm
  that spreads sign-ups across days and addresses is under the per-day threshold. This is the accepted cost of not
  keeping addresses.
- **Account age**: `registration_blocker()` applies no minimum account or history age; the trust score only breaks
  ties. A minimum is part of the open "define the eligibility model" decision and isn't invented here.
- **Web app on a shared computer**: no push token on the web, so only the address signals apply there.

## Owner decisions this raises
1. Confirm the seven thresholds above (listed under "Fraud detection thresholds set provisionally" in `PROJECT_TODO.md`).
2. Retention of the signal tables (30 / 90 / 180 days, DATA_INVENTORY.md "Retention"), and that they are not in the
   fan's data download.
3. Whether to add Cloud Armor (or a log-based alert) for request cadence at the edge.
4. `shared_device` at 3 accounts will flag families who share a phone; 2 would catch couples' second accounts but
   doubles the reviews. Keep 3 until the first pilot's review log says otherwise.
