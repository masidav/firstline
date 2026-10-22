-- A Spotify sync adds the fan's top artists that nobody tracks yet (worker SpotifySyncJob, Spotify__AutoTrackTopArtists).
-- How many the last sync added; openapi SpotifyStatus.top_artists_added.
ALTER TABLE spotify_connections
  ADD COLUMN top_artists_added integer NOT NULL DEFAULT 0 CHECK (top_artists_added >= 0);
