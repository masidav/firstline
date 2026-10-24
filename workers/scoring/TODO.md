# TODO — Scoring worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Consume score-recalculation jobs idempotently and coalesce redundant requests where appropriate.
- [x] Read active-artist signals, scoring rules, and non-reversed ledger points.
- [x] Apply the approved diminishing-returns formula to Spotify counts.
- [x] Generate or load the artist's synthetic baseline curve.
- [x] Calculate the real-user empirical distribution and blend it with the synthetic curve.
- [x] Set the estimated flag using the approved threshold/definition.
- [x] Persist user_artist_scores for active tracked artists only.
- [ ] Invalidate/update Redis cache entries after successful writes. *No Redis yet; not needed at this size.*
- [x] Handle empty populations, missing baselines, malformed curve data, and partial failures.
- [x] Add deterministic tests for formula behavior, percentile monotonicity, and repeatable results.
- [x] Score the wider Spotify signals (13 more rules, migration 0015, 2026-10-24): shares in `v_raw_scores`, counts
      and shares in the breakdown, `album_saves_score()` for thresholds. Verified: invariants and `ScoringJobTests`.

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. 2026-10-21: ScoringJobTests 7/7, full suite 128/128.
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
