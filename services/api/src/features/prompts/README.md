# Music prompts API

Migrations 0025 and 0034 (weekly since 2026-10-27). One open question a week for every fan, out on Thursdays (UTC)
and open until the next Thursday (`prompt_week_day()`; `prompts.day` holds that Thursday), for example "What's your best
hypothetical band name?".

## Where things live
- `prompt_library`: 30 built-in prompts. A day without a staff prompt gets the next one in rotation, written into
  `prompts` the first time anyone asks (`prompt_for_day()`), so everyone answers the same question all day.
- `prompts`: one per day, `source` `staff` or `library`. A prompt fans have answered can't be changed or removed.
- `prompt_answers`: one per fan per prompt, 1 to 280 characters, `public` or `followers`. Written, edited, or withdrawn
  only on the prompt's day; staff takedowns (`removed_at`) are permanent. Deleted accounts' answers are hidden at once
  (live authors only) and removed when the account's content is purged after 30 days.

## Endpoints
- `GET /prompts/today`: the prompt, the caller's answer, and up to three answers from people they follow, the last
  only once the caller has answered.
- `PUT /prompts/{id}/answer` (`body`, `visibility`), `DELETE /prompts/{id}/answer`: on the day only (`prompt_closed`).
- `GET /prompts/{id}/answers`: the caller's first, then people they follow, then everyone's public ones.
- Staff: `GET /admin/prompts`, `PUT /admin/prompts/{day}` (a Thursday, this week's or later), `DELETE /admin/prompts/{day}`.

Answers from the last week are a feed item (`prompt_answer`), reportable (`POST /reports` with `target_type:
prompt_answer`) and taken down from the staff queue like logs and photos. No points: answers are opinions, and points
would invite spam.

Tests: `tests/FandomScore.Tests/PromptApiTests.cs`, `database/tests/invariants.sql` (0025 section).
