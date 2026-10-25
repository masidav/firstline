# TODO — Follows API

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Follow/unfollow without approval, remove a follower, following and followers lists, fan search, and the
      public page with counts (migration 0018, 2026-10-25). Verified: `dotnet test` (FollowApiTests 3, invariants).
- [x] Followers-only album logs visible to followers (journal reads, reports, and the feed). Verified: FollowApiTests.
- [ ] Blocking: not wanted for now (decided 2026-10-24); revisit if abuse shows up.
- [ ] Profile visibility settings (public / followers-only stats) once Phase 15 decides what is public by default.
- [ ] Rate-limit follow churn if it is abused (follow/unfollow loops).

## Verification
- [x] Automated tests: `tests/FandomScore.Tests/FollowApiTests.cs`, `database/tests/invariants.sql` (0018 section).
