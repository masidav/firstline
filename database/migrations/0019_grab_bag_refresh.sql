-- Grab-bag songs keep Spotify's title, album, and cover on the row (0018). The worker's grab_bag.refresh_songs job
-- (daily) re-reads songs not refreshed for a week, like the weekly catalog refresh (ADR 0006), so renamed tracks
-- and new artwork show up. refreshed_at is the last successful read; null for songs never refreshed since added.
ALTER TABLE grab_bag_songs ADD COLUMN refreshed_at timestamptz;
CREATE INDEX grab_bag_songs_refresh_idx ON grab_bag_songs (COALESCE(refreshed_at, added_at));
