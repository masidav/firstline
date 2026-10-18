# TODO — Scoring worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Consume score-recalculation jobs idempotently and coalesce redundant requests where appropriate.
- [ ] Read active-artist signals, scoring rules, and non-reversed ledger points.
- [ ] Apply the approved diminishing-returns formula to Spotify counts.
- [ ] Generate or load the artist's synthetic baseline curve.
- [ ] Calculate the real-user empirical distribution and blend it with the synthetic curve.
- [ ] Set the estimated flag using the approved threshold/definition.
- [ ] Persist user_artist_scores for active tracked artists only.
- [ ] Invalidate/update Redis cache entries after successful writes.
- [ ] Handle empty populations, missing baselines, malformed curve data, and partial failures.
- [ ] Add deterministic tests for formula behavior, percentile monotonicity, and repeatable results.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
