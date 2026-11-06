# Gaming the fandom score: known strategies and mitigations

A living list (Phase 10, "red-team the scoring rules before each pilot"). Started 2026-10-29. Fandom scores decide who
gets face-value tickets, so every signal is an attack surface. Update this before each pilot: add what was tried, what
caught it, and what didn't.

What the score reads (`scoring_rules`, `v_raw_scores`, ADR 0005): Spotify likes, playlist tracks and playlists,
dedicated playlists, follows, top-artist rank, recent plays, the play ledger (90 days), play streak (weeks), catalog
coverage, deep cuts, album saves and how early they were saved; plus ledger points (album journal, verified proof,
trivia). Most Spotify signals saturate on a log or linear curve, so volume past saturation buys nothing.

## Strategies

| # | Strategy | What it gets | Mitigation now | Gaps |
|---|---|---|---|---|
| 1 | **Stream with a bot**: loop the artist 24/7 on a spare account | Plays, play history, streak, top-artist rank | Plays saturate (100 in 90 days); rules `round_the_clock`, `rapid_plays`, `looping` flag it (0043); trust breaks ties on account age and steady weeks | A bot that plays at human hours and pace, under the thresholds, isn't caught by the rules |
| 2 | **Streaming farm**: many accounts each streaming a little | Many mid-ranked fans for one show | One Spotify account per fan account (unique `spotify_user_id`); registration needs a score and a hub city; one registration per show; since 0054 (2026-11-01) `shared_device` flags 2+ accounts on one phone (2 since 0055, decided 2026-11-02; was 3), `shared_address_signups` 5+ sign-ups from one address in a day, `shared_address_registrations` 5+ accounts registering for one show from one address in a day, `spotify_account_reuse` one Spotify account passed through 3+ fan accounts (`bot-and-shared-device-detection.md`) | A farm on separate phones, networks, and Spotify accounts, or one that spreads sign-ups across days, isn't linked (addresses are hashed with a daily salt by design); no payment signals (no payments); one person per account (phone or ID) isn't built (Phase 10) |
| 3 | **Cram before the sale**: binge an artist the week registration opens | Recent plays, play history | `listening_spike` flags a jump of 10x over the fan's usual week while the artist has fan tickets in sight (0043); streak and tenure signals can't be crammed | A real new fan bingeing is flagged too: staff must review (an open rule flag only blocks registering) |
| 4 | **Click everything**: like every track, save every album, follow, add the catalog to playlists in ten minutes | Likes, playlists, dedicated playlists, follow, catalog coverage, album saves | Each signal saturates; album-save tenure rewards *early* saves; streak needs weeks; the allocation threshold is a raw-score floor; `burst_engagement` flags 40+ likes, playlist adds, album saves, and the follow of one artist inside 24 hours, holding 80 % of the fan's saves of that artist, within a week of sign-up or while the artist has fan tickets in sight (0045; playlist adds and the follow since 0048; undated items a fan already had when the sync first looked don't count, 0049) | Spotify's `added_at` on likes, album saves, and playlist items is what makes this detectable; the follow has no Spotify date, so it's dated when the daily sync first sees it (at most a day late). A clicker who spaces the saves over a few days, or stays under 40, isn't caught; a save undone before the next sync is never seen. A new fan saving a whole discography on joining is flagged too: staff review it |
| 5 | **Buy an aged Spotify account** with real history | Everything history-based | Trust counts *our* account age, not Spotify's; one Spotify account per fan account | A bought account with genuine history looks real. Proof of fandom (tickets, merch) and attendance history would help |
| 6 | **Share one strong account** among friends for different shows | Several shows' tickets | One registration per show per account; tickets are non-transferable and delivered to the holder 24 hours before | Different shows by the same artist aren't limited |
| 7 | **Farm ledger points**: journal logs, trivia, proof submissions | Ledger points | Points are small next to Spotify signals (trivia 0.30); proof is verified by staff; reported logs can be taken down (points reversed) | How much ledger points weigh against the Spotify score is still an open decision |
| 8 | **Name a rival as your +1** to lock them out | Blocks a competitor | Fixed 2026-10-28: the named guest is told and can decline | — |
| 9 | **Register many fans, return seats strategically** to steer backfill | Seats for friends lower in the ranking | Returned seats go strictly by draw rank; a held fan is skipped (0042) | — |
| 10 | **Evade detection after a flag**: stop, wait out the review, resume | Clean slate | A cleared rule waits 30 days before firing again; staff see the flag history and the eligibility audit | After the cooldown the same pattern fires again, which is intended |
| 11 | **Script the API**: sign up and register from a script, poll the standing, never open the app | Registrations at scale, first in line when a sale opens | Since 0054: `headless_registration` (registering without the eligibility check the app always makes, twice in 30 days), `instant_registration` (two registrations within 10 minutes of sign-up), `eligibility_polling` (300+ checks of one show in a day); the rate limiter (1 200/min) and the one-registration-per-show rule stay as they were | A script that mimics the app's calls, waits, and registers once per account isn't seen; request cadence isn't measured (no per-request log is kept, by choice); Cloud Armor at the edge is an owner decision |
| 12 | **Claim a hub city you're not in**: pick Chicago as a hub from anywhere to be "local" to every Chicago show | First in line in the draw (hub fans go first) | Since 0057 (2026-11-05): location checks the fan chooses to share (`hub-location-checks.md`): a match confirms the hub for trust; `hub_location_mismatch` flags 2+ checks on distinct days each 500+ km from every hub; `impossible_travel` flags one account checking in from two cities 1 500+ km apart within 6 hours | A fan who never shares their location is never seen (sharing is optional by design); a spoofed GPS looks real; a fan who moved or is travelling is flagged for review |
| 13 | **A second service as a second vote**: connect an Apple Music or Tidal account with the same bought history, hoping it adds up | Double credit | Since 0058 (ADR 0007): the score takes the strongest signal per artist across services, never the sum; a Tidal account connects to one fan account, and its hash joins the reuse history | Apple Music has no account id, so one Apple Music library can back several fan accounts; neither service gives play times, so the play-pattern rules don't see them |

## Detection rules (migrations 0043, 0045, 0048, 0049, 0054)
Thresholds are data in `fraud_rules` and provisional (PROJECT_TODO.md). An automatic flag only blocks registering
until staff uphold it (decided 2026-10-29), so a false positive costs a real fan a review, not a seat already won. The
fan isn't told about it until then, and the refusal is deliberately vague (decided 2026-10-30), so a gamer can't use
the rules as a test harness.

| Rule | Flags as | Fires when (starting values) |
|---|---|---|
| `round_the_clock` | bot | Plays in at least 20 distinct hours of a UTC day, on 3 of the last 14 days |
| `rapid_plays` | bot | Of 100 or more plays in 7 days, at least 70 % started under 40 seconds after the previous one |
| `looping` | streaming_farm | 300 or more plays of one artist in 7 days, 90 % on its top 3 tracks, for an artist with 10+ catalog tracks |
| `listening_spike` | streaming_farm | 300 or more plays of an artist in 7 days and 10x the weekly average of the 8 weeks before, while the artist has fan tickets open or opening within 14 days |
| `burst_engagement` | other | 40 or more liked tracks, playlist adds, album saves, and the follow of one artist within 24 hours (window started in the last 14 days), at least 80 % of the fan's saves of that artist, and the window started within 7 days of sign-up or the artist has fan tickets open or opening within 14 days. The evidence breaks the burst down by kind |
| `shared_device` | shared_device | A push device the fan used in the last 90 days was registered by 2 or more accounts in that time (0054; 2 since 0055, decided 2026-11-02) |
| `shared_address_signups` | duplicate_account | 5 or more accounts created from the fan's sign-up address on the same UTC day, in the last 30 days (0054) |
| `shared_address_registrations` | duplicate_account | 5 or more accounts registered for the same allocation from the same address on the same UTC day, in the last 30 days (0054) |
| `spotify_account_reuse` | duplicate_account | The fan's Spotify account was linked to 2 or more other fan accounts in the last 180 days, or the fan connected 3 or more Spotify accounts (0054) |
| `headless_registration` | bot | 2 or more registrations in 30 days with no eligibility check of that allocation before registering (0054) |
| `instant_registration` | bot | 2 or more registrations within 10 minutes of creating the account, in the last 30 days (0054) |
| `eligibility_polling` | bot | 300 or more eligibility checks of one allocation in one UTC day, in the last 7 days (0054) |
| `hub_location_mismatch` | other | 2 or more location checks on distinct days in the last 90 days, each 500 km or more from every hub city the fan had then (0057) |
| `impossible_travel` | duplicate_account | Two location checks within 6 hours whose nearest cities are 1 500 km or more apart, in the last 30 days (0057) |

The 0054 rules read short-lived, hashed or counted signals (address hashes with a daily salt for 30 days, push token
and Spotify account history for 90 and 180 days, daily eligibility counts for 90 days), all deleted with the account;
the evidence shows counts and categories, never an identifier. What they can and can't see, and the privacy
trade-off, is in `bot-and-shared-device-detection.md`.

Limits of the data: the play ledger only has tracked artists, polled hourly, and Spotify returns at most 50 plays per
poll, so very heavy listening is undercounted (it can't inflate a rule's count, only hide some of it). Likes, playlist
tracks, and album saves are snapshots kept current by the daily sync, but each carries Spotify's own `added_at`, so
`burst_engagement` reads the time of the save, not of the sync; a save undone before the next sync is never seen. The
follow is a boolean on Spotify's side: it's dated when the sync first sees it (`followed_since`, cleared on unfollow),
so a follow is placed within a day of when it happened. A playlist item Spotify doesn't date (some very old playlists)
is dated the same way, and keeps that date across syncs.

Resolved 2026-10-30 (migration 0049; decided 2026-10-30: first-sync undated items don't count): every undated item a
fan already has used to be stamped by the same sync (their first, or the first after 0048), so a long-time fan with a
big, very old playlist of one artist looked like a burst and was held from registering until staff cleared it. Now an
undated playlist item or follow that was already there the first time the sync could see it (the fan's first sync, or
the first after the artist became active) is marked baseline (`saved_at_baseline`, `followed_since_baseline`): its date
is unknown and it never starts or joins a burst, though it still counts among the fan's saves of the artist for the
80 % share. Only undated items that appear between two syncs, and items Spotify dates, count. The flag is fixed for
the life of the row, so a re-sync can't change it; rows stamped before 0049 were all made baseline. What this gives
up: undated items (very old playlists, and every follow) added just before a fan's first sync are not seen as a
burst; a clicker's likes, album saves, and new playlists still are, because Spotify dates them.

## Before each pilot
- Re-read this table against the current `scoring_rules` and `fraud_rules`.
- Try the strategies on a test account on beta; record what the score did and whether a rule fired.
- Check the flags raised so far: how many staff cleared (false positives) and why; tune `fraud_rules.params`.
