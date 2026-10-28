# Artist, merch, and ticket links

Migration 0038 (2026-10-27).

- **Links:** an artist's `website_url` and `merch_url` (https only, `artists_website_https` / `artists_merch_https`). Staff
  set or clear them with `PATCH /v1/admin/artists/{id}/links` (the app's Artist links and sources screen). The concert
  worker fills the website from Ticketmaster's homepage link the first time it loads a newly linked artist, if none is
  set (`workers/concerts`). The app shows Merch, Website, and Spotify buttons on the artist's page, and each show's
  "Tickets on Ticketmaster" button.
- **Clicks:** the app reports each tap with `POST /v1/clicks` (`artist_website`, `artist_merch`, `artist_spotify`,
  `event_tickets` with the show). Only the kind, artist, show, and time are kept: no user id, so nothing personal to
  export or scrub. Staff see counts per artist and kind with `GET /v1/admin/clicks?days=30`. A click is never shown or
  treated as a purchase; purchase confirmation needs a ticketing partner (PROJECT_TODO.md Phase 8).

The older `outbound_clicks` and `share_cards` tables from the first schema were never used; `link_clicks` replaces
`outbound_clicks` (anonymous by design), and score cards are drawn on request instead of stored. Both old tables stay
empty and can be dropped in a later migration.

Not done: link clicks aren't pruned (rows are tiny; add pruning if the table grows), and artists can't see their own
counts yet (that comes with promoter and artist accounts). Tests: `tests/FandomScore.Tests/LinkApiTests.cs`,
`ConcertSyncJobTests.cs` (website from Ticketmaster), invariants for 0038; app `src/features/links/openLink.test.tsx`.
