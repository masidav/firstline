# Fandom score (fan app)

Your score for one artist, at `/artist/{artistId}/score` (`ScoreScreen`), reached from the "Score" action on the
home screen's "Your artists" rows.

- **Data:** `GET /artists/{id}` (name) and `GET /artists/{id}/score`. `score.ts` turns the score's `breakdown`
  into display rows: one per Spotify signal the server reported a share for (disabled rules are left out),
  grouped as Listening, Library, and Playlists and follows, each with the count behind it and a bar for its share.
- **States:** loading; error with retry; `artist_onboarding` (catalog still loading); `computing` (polls every
  4 s up to 8 times, plus a Refresh button that calls `POST /artists/{id}/score/refresh`); `ready`; and a 404
  (paused or unknown artist) shown as "not available".
- **Estimates:** when `is_estimated` is true the headline carries "estimate", and the text says what share of
  the percentile still comes from the typical-fan model (`baseline_weight`). Every ready score explains that
  percentiles rank fans who use the app, blended with that model, not every fan in the world.
- **Points line:** Spotify points + journal and proof points = total, from the breakdown.

Tests: `score.test.ts` (wording, tenure, rows) and `ScoreScreen.test.tsx` (every state, refresh, retry, links).

## Known limitations
- No link to the share-card flow yet; that flow is Phase 8 work in the sharing feature.
- Signal labels are the app's own wording for the rules in `workers/scoring/README.md`; adding a rule on the
  server needs a row in `score.ts` to be shown.
