-- Artist image from Spotify, fetched by the server when a user adds the artist (POST /artists).
-- openapi Artist.image_url. See docs/adr/0005-spotify-integration.md.
ALTER TABLE artists ADD COLUMN image_url text;
