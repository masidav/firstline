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
| 2 | **Streaming farm**: many accounts each streaming a little | Many mid-ranked fans for one show | One Spotify account per fan account (unique `spotify_user_id`); registration needs a score and a hub city; one registration per show | No device, network, or payment signals yet, so linked accounts aren't detected; one person per account (phone or ID) isn't built (Phase 10) |
| 3 | **Cram before the sale**: binge an artist the week registration opens | Recent plays, play history | `listening_spike` flags a jump of 10x over the fan's usual week while the artist has fan tickets in sight (0043); streak and tenure signals can't be crammed | A real new fan bingeing is flagged too: staff must review (an open rule flag only blocks registering) |
| 4 | **Click everything**: like every track, save every album, follow, add the catalog to playlists in ten minutes | Likes, playlists, dedicated playlists, follow, catalog coverage, album saves | Each signal saturates; album-save tenure rewards *early* saves; streak needs weeks; the allocation threshold is a raw-score floor; `burst_engagement` flags 40+ likes, playlist adds, album saves, and the follow of one artist inside 24 hours, holding 80 % of the fan's saves of that artist, within a week of sign-up or while the artist has fan tickets in sight (0045; playlist adds and the follow since 0048) | Spotify's `added_at` on likes, album saves, and playlist items is what makes this detectable; the follow has no Spotify date, so it's dated when the daily sync first sees it (at most a day late). A clicker who spaces the saves over a few days, or stays under 40, isn't caught; a save undone before the next sync is never seen. A new fan saving a whole discography on joining is flagged too: staff review it |
| 5 | **Buy an aged Spotify account** with real history | Everything history-based | Trust counts *our* account age, not Spotify's; one Spotify account per fan account | A bought account with genuine history looks real. Proof of fandom (tickets, merch) and attendance history would help |
| 6 | **Share one strong account** among friends for different shows | Several shows' tickets | One registration per show per account; tickets are non-transferable and delivered to the holder 24 hours before | Different shows by the same artist aren't limited |
| 7 | **Farm ledger points**: journal logs, trivia, proof submissions | Ledger points | Points are small next to Spotify signals (trivia 0.30); proof is verified by staff; reported logs can be taken down (points reversed) | How much ledger points weigh against the Spotify score is still an open decision |
| 8 | **Name a rival as your +1** to lock them out | Blocks a competitor | Fixed 2026-10-28: the named guest is told and can decline | — |
| 9 | **Register many fans, return seats strategically** to steer backfill | Seats for friends lower in the ranking | Returned seats go strictly by draw rank; a held fan is skipped (0042) | — |
| 10 | **Evade detection after a flag**: stop, wait out the review, resume | Clean slate | A cleared rule waits 30 days before firing again; staff see the flag history and the eligibility audit | After the cooldown the same pattern fires again, which is intended |

## Detection rules (migrations 0043, 0045, 0048)
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

Limits of the data: the play ledger only has tracked artists, polled hourly, and Spotify returns at most 50 plays per
poll, so very heavy listening is undercounted (it can't inflate a rule's count, only hide some of it). Likes, playlist
tracks, and album saves are snapshots kept current by the daily sync, but each carries Spotify's own `added_at`, so
`burst_engagement` reads the time of the save, not of the sync; a save undone before the next sync is never seen. The
follow is a boolean on Spotify's side: it's dated when the sync first sees it (`followed_since`, cleared on unfollow),
so a follow is placed within a day of when it happened. A playlist item Spotify doesn't date (some very old playlists)
is dated the same way, and keeps that date across syncs. The flip side: every undated item a fan already has is
stamped by the same sync (their first, or the first after 0048), so a long-time fan with a big, very old playlist of
one artist can look like a burst and go to staff review; the evidence's `kinds` shows it is playlist adds.

## Before each pilot
- Re-read this table against the current `scoring_rules` and `fraud_rules`.
- Try the strategies on a test account on beta; record what the score did and whether a rule fired.
- Check the flags raised so far: how many staff cleared (false positives) and why; tune `fraud_rules.params`.
