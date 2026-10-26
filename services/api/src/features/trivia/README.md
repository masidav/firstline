# Daily trivia API

Migrations 0022 and 0024 (decided 2026-10-25). One question a day (UTC) per fan, about one of their active scored
artists. Fans of the same artist share the day's question (`trivia_daily_questions`, made from the stored catalog by the
first fan who asks; a fan joins a question others are already playing when one of their artists has one); each fan's
copy in `trivia_questions` holds their answer. Kinds (`GET /me/trivia`):

- `track_album`: "Which {artist} album is "{song}" on?" with up to four album titles. Needs two albums; title tracks
  and songs on two albums are skipped so there is exactly one right answer.
- `release_year`: "What year did {artist} release "{album}"?" with four years around the right one (none in the future).

`POST /me/trivia/{id}/answer` (`answer_trivia()`) takes the fan's pick once, on that day only (`trivia_answered`,
`trivia_expired` otherwise). A right answer earns `trivia_correct`: 0.30 ledger points, a tenth of `album_logged`, at
most once a day, only for active artists, reversible like every award. The right answer isn't sent until the fan has
answered. Change the points with `PUT /v1/admin/scoring-rules/trivia_correct`.

Once the fan has answered, `results` shows how the shared question went: fans who answered (the caller included), how
many got it right and the percent, and up to two people the fan follows who got it right, first to answer first
(decided 2026-10-25). Nothing about it is sent before the fan answers, so the results can't give the answer away.

Tests: `tests/FandomScore.Tests/TriviaApiTests.cs`, `database/tests/invariants.sql` (0022 section).
