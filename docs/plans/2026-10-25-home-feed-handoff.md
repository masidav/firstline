# Handoff: home feed, follows, grab bags (paused 2026-10-25, completed later the same day)

**Status: done.** Everything under "To finish the feature" was completed on 2026-10-25: CI for `38ca7ac` was green
(six jobs), the full suite passed (216/216 before, and again after the additions), `tools/dev/seed.py` seeds follows
and grab bags, the bouncer door exists (`GET /me/bouncer-door`, app `/bouncer-door`), and the worker refreshes
song titles and covers daily (`grab_bag.refresh_songs`, migration 0019). The seed also writes placeholder photo
files for shared merch, so feed photo cards show an image. The rest of this file is the record of the
pause and still describes how the feed order works and the repo's working notes.

## State when paused
- Everything is committed and pushed to `main`: commit `38ca7ac` ("Home feed as one vertical scroll: follows, grab
  bags, shows, registrations, stats"). Working tree clean; no stray processes or containers from this work.
- What it added: migration `0018_follows_grab_bags.sql` (follows, grab_bag_songs, `artists.added_via`, a new
  `track_artist(..., p_via)`), API features `follows`, `grab-bag`, a rewritten `feed` (seeded, type-weighted random
  mix), Spotify track search/lookup in `packages/spotify`, contract changes in `openapi.yaml`, and the fan app's
  FlatList home feed with new cards, `/grab-bag` and `/people` screens. Details: `services/api/src/features/feed/README.md`,
  `services/api/src/features/follows/README.md`, `services/api/src/features/grab-bag/README.md`, and the matching
  `apps/mobile/src/features/*/README.md`. Roadmap entries updated in `PROJECT_TODO.md` (Phase 6, 7, 8, 15).

## Verification done, and the one gap
- Backend: full `dotnet test` ran once at 215/216; the failure was the old "newest first" feed assertion in
  `ProofApiTests`, updated for the shuffled mix. After the final change to the feed's ordering (type weights), the
  Feed, Proof, Report, Follow, GrabBag, and database invariant test classes were rerun green. **A last full-suite
  run was not done** (declined at pause time). First thing on resume: `dotnet build FandomScore.slnx` and
  `dotnet test FandomScore.slnx`, and check the CI run for commit `38ca7ac` on GitHub (`.github/workflows/ci.yml`;
  this session had no GitHub API access to confirm it).
- App: `npm run typecheck` clean, `npm test` 108/108, `npm run generate:api` produces no diff.
- Browser: clicked through in Chrome at phone size against a seeded throwaway database (sign-in, scroll paging with
  one seed, Shuffle, Follow, People, grab bag). The throwaway database was removed afterwards.

## To finish the feature
1. Confirm CI is green for `38ca7ac`; if a job failed, fix forward on `main` (direct commits, no PRs; see CLAUDE.md).
2. `tools/dev/seed.py`: seed follows and grab-bag songs so a fresh dev database shows a lively feed (listed in
   `services/api/src/features/grab-bag/TODO.md`). Without it, a seeded fan's feed is logs, photos, shows, and their
   own registrations only.
3. Bouncer door: draw a song from followed fans' grab bags and show whose it is (decided 2026-10-20, not built).
4. A refresh job for stored grab-bag song titles/covers (weekly, like the catalog refresh).
5. Optional polish seen in the browser run: a shared photo card with no stored file shows no image area at all; the
   seed's submissions have no files, so this is a seed gap rather than a bug.

## How the feed order works (so changes stay consistent)
`FeedEndpoints.CandidatesSql`: each source contributes its newest 100 items; a seeded hash orders items within a type;
across types each type is dealt out by weight (album_log 3, photo/grab_bag/stat 2, concert/registration 1) with a
seeded jitter, so every page mixes types. The cursor is an offset over that order; the app keeps `seed` while paging
and picks a new one on Shuffle or pull-to-refresh. `FeedApiTests` checks that one seed pages consistently and
another reshuffles; keep that property if the ordering changes.

## Working notes for this repo
- The pre-tool hook blocks any Bash command that mentions an applied migration path together with something that
  looks like a modifying command (even `sed -n` in a pipeline). Use the Read tool for applied migrations.
- The hook also blocks commands mentioning `.env*` files (including `apps/mobile/.env.local`); pass settings as
  environment variables on the command line instead.
- `pkill -f <pattern>` inside a Bash call kills the call's own shell when the pattern appears in the command text
  (exit 144); use `pkill -x <process name>`.
- For a browser check: `docker run` a `postgres:16` on another port, `dotnet run --project database` with
  `ConnectionStrings__Database`, pipe `python3 tools/dev/seed.py --sql` into `psql`, run the API with
  `ASPNETCORE_ENVIRONMENT=Development Auth__DevSignIn=true Cors__AllowedOrigins=http://localhost:8081`, and the app
  with `CI=1 EXPO_PUBLIC_DEV_SIGN_IN=true npx expo start --web`. Expo rewrites `apps/mobile/tsconfig.json` on start;
  `git checkout` it before committing. `playwright-core` with `/usr/bin/google-chrome` works for screenshots.
- Observed 2026-10-26: with `CI=1`, that Expo server kept serving the first bundle after source edits (Metro did
  not pick up changes); started without `CI=1` (`< /dev/null`) it rebuilt on every edit. `apps/mobile/.env.local`
  sets `EXPO_PUBLIC_API_URL` and wins over the same variable on the command line, so run the API where it points
  (`http://127.0.0.1:5271`). `dotnet run` takes its port from the launch profile; pass `--no-launch-profile` for
  `ASPNETCORE_URLS` to apply. `pgrep -f <pattern>` matches the calling shell too; write the pattern as `[a]pi` to
  avoid killing it. To run the API from a clean `main` while another session has uncommitted changes, use
  `git worktree add --detach /tmp/fs-main HEAD`.
- CI also runs `tools/ci/check_migration_safety.py` on new migrations (rollout safety: no NOT NULL on an existing
  column, no drops or renames, unless the file says `-- allow-breaking: <why>`). `dotnet test` doesn't run it, so run
  it on a new migration before pushing. A migration already on `main` can't be edited to add the comment; a reviewed
  exception goes in `tools/ci/migration_safety_allowances.txt` instead.
