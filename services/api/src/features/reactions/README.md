# Reactions API

Migration 0026 (decided 2026-10-25: emoji reactions instead of comments). Fans react to posts they can see (shared
photos, album logs, prompt answers) with a fixed set: `fire` 🔥, `heart` ❤️, `laugh` 😂, `rock` 🤘, `wow` 😮, `clap` 👏.
Several different ones per post; the same one twice is still one; any can be taken back. No points.

- `PUT /reactions/{photo|log|prompt_answer}/{id}/{reaction}` and `DELETE` the same: both return the post's counts.
- The feed carries `reactions` on photo, album log, and prompt answer items: `{reaction, count, mine}` in the fixed order.
- Rows are deleted with their post, and a deleted account's reactions are deleted at once.

Custom emoji from Shelby are a planned addition: reactions are stored by key, so they are new keys with artwork.

Tests: `tests/FandomScore.Tests/ReactionApiTests.cs`, `database/tests/invariants.sql` (0026 section).
