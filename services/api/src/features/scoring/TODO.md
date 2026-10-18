# TODO — Scoring domain/API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Document the exact raw-score formula using Spotify state and non-reversed ledger points.
- [ ] Load score weights from scoring_rules rather than hard-coding them in application code.
- [ ] Implement diminishing returns or caps for large Spotify counts and document the selected formula.
- [ ] Generate and store a synthetic baseline for each artist before activation.
- [ ] Implement real-user ECDF and synthetic CDF blending using w = n / (n + k).
- [ ] Keep blend_k configurable and treat its current value as a proposed starting value until confirmed.
- [ ] Mark a score estimated while the configured synthetic-baseline dominance condition applies.
- [ ] Compute scores only for active tracked artists.
- [ ] Write computed results to user_artist_scores and use Redis only where it provides a defined performance benefit.
- [ ] Define when scores are invalidated and recomputed after syncs, logs, comments, submissions, or reversals.
- [ ] Add tests for empty populations, zero scores, ties, extreme values, baseline blending, and paused artists.
- [ ] Document score interpretation and ensure UI copy does not imply ranking against all real-world fans.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
