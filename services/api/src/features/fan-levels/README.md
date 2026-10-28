# Overall fan scores: trust and fan level

Migration 0039 (decided 2026-10-28). Two scores per fan across all artists, each the equal-weight average of parts that
run from 0 to 1 and saturate at a cap. Weights and caps are provisional (PROJECT_TODO.md).

| | Trust (never shown) | Fan level (shown) |
| --- | --- | --- |
| Used for | Breaking ties in fan ticket draws, nothing else | Status: Profile, fan pages, feed bylines; never tickets |
| Parts | account age (full at 180 days), verified proof (3), steady listening (weeks with plays, of the last 12), good standing (lowered by posts staff took down in the last year) | fandom (average of the three best percentiles), albums logged (log curve, full at 50), verified proof (5), trivia answered right (10), prompts answered (10) |
| Result | 0 to 1 | levels 1 to 5: below 0.2 is 1, 0.8 and up is 5 |
| Functions | `fan_trust_parts()` | `fan_activity_parts()`, `fan_level_for()` |

- **Draws:** `run_allocation_selection()` computes each live registration's trust fresh and stores it
  (`allocation_trust_snapshots`, written once, never edited); `allocation_ranking()` orders by hub city, then the
  registration's snapshot raw score, then trust, then the stored seed. Trust only decides between equal raw scores.
  Before a draw there are no snapshots, so standings are unchanged.
- **Stored levels:** the worker's hourly `fans.overall_scores` job (`refresh_fan_overall_scores()`) writes
  `fan_overall_scores` for every live fan; deleted accounts' rows go.
- **API:** `GET /v1/me/level` computes the caller's level and parts fresh. `fan_level` on `GET /v1/users/{handle}` and
  `user_level` on feed items come from the stored levels, for the fan and their followers only (like other stats).
- "This or that" and blind rankings join both scores with a very small share once they exist.

Tests: `tests/FandomScore.Tests/FanScoresTests.cs` (tie-breaks over six seeds, score beats trust, snapshots, levels,
the job), `FanLevelApiTests.cs`, invariants for 0039; app `FanLevelSection.test.tsx`, `FanProfileScreen.test.tsx`.
