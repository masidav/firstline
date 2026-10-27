-- Outbound link clicks (Phase 8, 2026-10-27): fans tap an artist's website, merch store, or Spotify page, or a show's
-- ticket link; each tap is counted so staff and artists can see interest. A click is a click: nothing here says a
-- purchase happened (purchase confirmation needs a ticketing partner). No user id is kept: the counts are all we need,
-- and an anonymous row has nothing to export or scrub.

CREATE TABLE link_clicks (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  kind       text NOT NULL CONSTRAINT link_clicks_kind_check
               CHECK (kind IN ('artist_website', 'artist_merch', 'artist_spotify', 'event_tickets')),
  artist_id  uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  event_id   uuid REFERENCES events ON DELETE CASCADE,
  clicked_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT link_clicks_event_kind CHECK ((kind = 'event_tickets') = (event_id IS NOT NULL))
);
CREATE INDEX link_clicks_artist_idx ON link_clicks (artist_id, clicked_at);

-- Artist links are https only (they open from the app).
ALTER TABLE artists
  ADD CONSTRAINT artists_website_https CHECK (website_url IS NULL OR website_url ~ '^https://') NOT VALID,
  ADD CONSTRAINT artists_merch_https CHECK (merch_url IS NULL OR merch_url ~ '^https://') NOT VALID;
