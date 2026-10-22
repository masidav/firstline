# Scoring worker

Turns each fan's Spotify signals (and, later, ledger points) into a score and a percentile per active artist.

## The score
Each Spotify signal earns a share from 0 to 1 (`v_raw_scores`, migration 0008):

| Rule | Signal | Share |
| --- | --- | --- |
| `spotify_liked_track` | liked tracks by the artist | `ln(1 + n) / ln(1 + saturation)`, at most 1 |
| `spotify_playlist_track` | distinct tracks in the fan's own or collaborative playlists | same log curve |
| `spotify_album_depth` | distinct releases the fan has tracks from | `n / min(saturation, catalog size)`, at most 1 |
| `spotify_top_artist` | rank in the fan's Spotify top artists | rank 1 → 1, rank `saturation` → 1/saturation, unranked → 0 |
| `spotify_recent_play` | plays among the last 50 | same log curve |

The log curve gives diminishing returns: 10 liked tracks of a saturation of 100 earn about half marks.

**Spotify score** (0–100) = 100 × Σ(weight × share) / Σ(weight), over active Spotify rules. **Raw score** = Spotify
score + ledger points (journal, proof of fandom; none exist yet).

Weights are the `points` of the `spotify_*` rows in `scoring_rules`. They are relative shares and start as an even
split (all 1, decided 2026-10-21). Change them often:
- locally: `testScripts/scoringWeights.sh spotify_liked_track 2` (lists the rules without arguments);
- elsewhere: `PUT /v1/admin/scoring-rules/{rule_key}` (staff), with `points`, `saturation`, or `active`.

Any change to `scoring_rules` queues a rescore of every active artist (`trg_scoring_rules_rescore`).

## The percentile
`ScoreArtistJob` (`scoring.score_artist`, one artist per job) ranks all of the artist's fans: the real fans'
mid-rank percentile is blended with the artist's synthetic baseline, `p = w × real + (1 − w) × synthetic`,
`w = n / (n + k)`. While the baseline carries more than half the weight, the score is marked `is_estimated`.
Provisional baselines use the current `Baseline__*` settings (`MedianScore` 15 on the 0–100 scale, `Sigma` 1,
`BlendK` 200; still an open decision), so tuning them needs no data migration.

## When scores are recomputed
- after every Spotify sync, for the artists the fan had and has signals for;
- when a weight changes, for every active artist;
- on request (`POST /v1/artists/{id}/score/refresh`);
- daily for every active artist (`scoring.rescore_all`), as a safety net.

Paused or deleted artists have their scores removed. At most one rescore is queued per artist.

## Known limitations
- `v_raw_scores` reads every fan's signals and ledger rows before filtering to one artist, so a full rescore costs
  artists × rows. Fine at this size; turn it into a per-artist function if rescoring gets slow.
- Ledger points are added on top of the 0–100 Spotify score; how much they should weigh is an open decision.
