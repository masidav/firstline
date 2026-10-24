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
| `spotify_top_artist_short` | rank in the fan's top artists of the last 4 weeks | same rank curve |
| `spotify_top_artist_long` | rank in the fan's all-time top artists | same rank curve |
| `spotify_top_track` | distinct tracks by the artist among the fan's top tracks (any range) | log curve |
| `spotify_saved_album` | the artist's albums the fan saved | `n / min(saturation, catalog size)` |
| `spotify_early_save` | releases saved within a week of release day (day-precision dates only) | log curve |
| `spotify_tenure` | days since the fan's earliest save of the artist (liked track or album) | `days / saturation`, at most 1 |
| `spotify_playlist_feature` | own/collaborative playlists with a track by the artist | log curve |
| `spotify_dedicated_playlist` | playlists that are at least 80 % the artist, with 5+ tracks | `n / saturation`, at most 1 |
| `spotify_follow` | follows the artist on Spotify (scope `user-follow-read`) | 1 or 0 |
| `spotify_play_history` | plays in the last 90 days, from the play ledger (`user_plays`, polled hourly) | log curve |
| `spotify_play_streak` | consecutive weeks with a play, counted back from this week (or last week) | `weeks / saturation`, at most 1 |
| `spotify_catalog_coverage` | catalog tracks the fan saved, put in a playlist, or played | `n / min(saturation, catalog tracks)` |
| `spotify_deep_cut` | of those tracks, the ones at most 10 % of the artist's fans here have (once the artist has 10 fans) | log curve |

The log curve gives diminishing returns: 10 liked tracks of a saturation of 100 earn about half marks. The 13
signals after `spotify_recent_play` were added by migration 0015 (2026-10-24), all at weight 1, so each of the 18
signals is worth at most 100/18 points until the weights are tuned. `album_saves_score(artist, saves)` returns the
raw score of the saved-album signal alone under the current rules (allocations use it as a threshold).

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
- `v_raw_scores` reads every fan's signals, saves, plays, and ledger rows before filtering to one artist, so a full
  rescore costs artists × rows (deep cuts also count fans per track). Fine at this size; turn it into a per-artist
  function if rescoring gets slow.
- Ledger points are added on top of a 0–100 Spotify score that is now spread over 18 signals, so a few journal or
  proof awards can outrank a strong listener. Weighing the two is the open decision in `PROJECT_TODO.md`.
- Ledger points are added on top of the 0–100 Spotify score; how much they should weigh is an open decision.
