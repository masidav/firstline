# More ways to use stats to verify fandom

Design note, 2026-11-05 (requested 2026-11-05: "think of more ways to use stats to verify fandom"). The score decides
who gets face-value tickets, so every signal is both a measure of fandom and an attack surface
(`docs/security/gaming-strategies.md`). This note lists signals we don't use yet, what each proves, how it can be
gamed, and what it would take. Each is marked **verify** (a check against gaming, feeds trust or a fraud rule),
**score** (a measure of fandom, feeds the raw score), or both. Nothing here is built unless the table says so.

What we have today (ADR 0005, 0007, migrations 0015, 0039, 0043 to 0058): 18 Spotify signals (likes, playlists,
album saves and when, top-artist ranks, top tracks, follows, 90-day plays, weekly streaks, catalog coverage, deep
cuts), now also from Apple Music and Tidal (strongest signal across services); ledger points from the journal,
verified proof, and trivia; a trust score (account age, verified proof, steady listening, good standing, hub
confirmed) that breaks ties; 15 fraud rules over play patterns, save bursts, devices, addresses, account reuse,
headless or instant registration, eligibility polling, and now location.

## Built today (2026-11-05)
| Signal | Kind | What it proves | How |
|---|---|---|---|
| **Cross-service agreement** | verify | A fan whose Apple Music or Tidal library shows the same artist as their Spotify is harder to fake than one with a single bought account; the score takes the strongest signal across services rather than the sum, so a second service confirms, it doesn't double | ADR 0007; `v_raw_scores` aggregates across `provider`. A trust part for "two services agree" is listed below, not built |
| **Hub confirmed by location** | verify | The fan was near a hub city when they chose it or registered | Migration 0057, trust part `hub_confirmed`, rules `hub_location_mismatch` and `impossible_travel` |

## Proposed, in rough order of value for cost
| # | Signal | Kind | What it proves | Gaming and false positives | What it needs |
|---|---|---|---|---|---|
| 1 | **Release-day listening** ("first-week fan"): plays or saves of a new release within 7 days of its release date, per artist over time | score + verify | The fan follows the artist as they go, not after a tour is announced. Hard to fake retroactively: the ledger has the dates | A gamer who follows release calendars can do it for one artist; it costs time per release, and it starts counting only from now | `user_plays` has times; `albums.release_date` exists. One CTE in `v_raw_scores` ("release-week plays, releases"), a rule row `spotify_release_week`. Cheap. We have `spotify_early_save` for saves already; this adds plays |
| 2 | **Listening across the catalog over time** (depth × time): how many distinct months the fan has plays of the artist, and how many distinct releases they played in each | score + verify | Years of listening, not a week of looping. Tenure today comes from the earliest save; this is tenure from plays | Can't be crammed; a bought aged Spotify account carries it (strategy 5) | Plays are kept 400 days (`Spotify__PlayHistoryRetention`), so at most 13 months. Decide whether to keep a monthly roll-up (artist, month, plays, releases) for good: tiny rows, no track detail. Then a `months_listening` signal with a linear share |
| 3 | **Human listening shape** (the inverse of the bot rules): plays spread over hours of the day and days of the week that look like a person (an entropy over hour-of-day buckets, a weekday/weekend mix), skips implied by short gaps | verify | 24/7 and metronomic patterns already flag; a healthy shape can raise trust | A careful bot schedules plays at human hours (gaming-strategies strategy 1 gap) | Reads `user_plays` only. A trust part `human_listening` in `fan_trust_parts` (0..1 from the hour-of-day entropy over 90 days, full at 12 of 24 hours used with no hour above 20 %). Cheap |
| 4 | **Two services agree** | verify | The fan's top artists on two independent services overlap | A gamer needs two paid accounts per fan account | Rows exist (0058). A trust part: 1 when two providers have signals for the same artist and both rank it in their top lists; 0.5 for one service. Cheap once a second provider is live |
| 5 | **Attendance history**: proof of a past ticket (verified submission) tied to a show in `events`, with the fan's location check near the venue's city on the show date | both | The fan went. The strongest fandom signal there is, and the one promoters care about | Ticket photos can be borrowed (OCR of order numbers catches reuse); a location check at the venue needs the fan to open the app at the show | `submissions` are verified by staff; `events` have city and date; `hub_location_checks` has the nearest city. Add kind `show_night` to location checks (prompted on the day of a show the fan registered for), a `verified_attendance` ledger rule, and a trust part. Medium; the Phase 10 item "verified attendance history" |
| 6 | **Setlist listening**: plays of songs from the artist's recent setlists (setlist.fm) in the weeks around a show | score | A fan who listens to what's played live | Public data, easy to game by one artist; low weight | A setlist source (setlist.fm API, terms to check), a table of (event, track) and a signal `setlist_plays`. Medium |
| 7 | **Journal matches listening**: an album log whose album the fan also played or saved around `listened_on` | verify | Journal points are earned by a listener, not a typist (strategy 7) | A gamer plays the album once; costs 40 minutes per log | Join `album_logs` with `user_plays`/`user_album_saves` within ±7 days. A ledger multiplier or a trust part `journal_backed`. Cheap |
| 8 | **Library diversity**: how many distinct tracked artists the fan has signals for, and how evenly plays spread across them | verify | A real listener listens to many artists; a farm account streams one | Easy to pad with likes of other artists (likes are free); plays are the honest measure | Reads `user_artist_signals` and `user_plays`. A trust part `listening_breadth` (full at 5 artists with plays in 90 days). Cheap, but weight it low: a fan of one band is real too |
| 9 | **Merch and store purchases** (direct-to-consumer store, Bandcamp) | both | Money spent is the hardest signal to fake in bulk | Needs a partner's data; privacy of purchase history | The Phase 10 item "direct-to-consumer store purchases"; an OAuth or CSV source per store. Large |
| 10 | **Fan-to-fan social proof**: followed by other verified fans of the same artist; reactions received on journal logs of that artist | score (small) | Known in the community | Sybil accounts follow each other (shared-device and address rules help) | `follows`, `post_reactions` exist. A `community` signal with a log curve, tiny weight. Cheap |
| 11 | **Streaming "wrapped"-style exports**: the fan uploads their Spotify extended streaming history export (years of plays with times) | score + verify | Years of history at once; dates that predate our tracking | Exports can be edited (a JSON file); checking against the live account's recent plays catches lazy forgery | The Phase 7 item "Spotify data export upload" through the submissions flow; a parser; dedupe against `user_plays`. Medium |
| 12 | **Early follower**: the fan's follow or first save predates the artist's first allocation on the platform, or the artist's tracking date | verify | Not here for the tickets | Only once per artist; a bought account carries the dates | `followed_since`, `first_saved_at`, `artists.tracked_at`, `allocations.published_at`. A trust part or a small score signal. Cheap |

## What not to do
- **Don't read what the fan doesn't give.** Everything above comes from accounts the fan connected or data they
  uploaded, or from the app's own records; nothing scrapes a profile.
- **Don't make trust visible as a number.** Trust breaks ties (decided 2026-10-28); showing it would turn it into a
  target and a status symbol. The fan-visible level stays activity-based.
- **Don't let one signal dominate.** Every score signal saturates; every trust part is one of several; every
  rule only asks for review.

## Suggested next three
1. Release-day listening (#1) and journal-backed logs (#7): both cheap, both reward the behaviour the product wants.
2. Human listening shape (#3) as a trust part: cheap, and the natural complement to the bot rules.
3. Attendance (#5): the biggest value, and it reuses the location checks built today.

Each needs an owner decision on weight before it ships; listed under Open decisions in `PROJECT_TODO.md`.
