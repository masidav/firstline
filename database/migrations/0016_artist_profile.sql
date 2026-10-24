-- Artist profiles (2026-10-24): a fan can mark themselves as an artist and add their band's links to their
-- page: the band's profile page (any https URL, e.g. a Spotify artist page or website), Instagram, and X.
-- Links are only kept while the artist flag is on; turning it off clears them (users_artist_links).
-- Social handles are stored bare (no leading @); the app builds the instagram.com / x.com links.

ALTER TABLE users
  ADD COLUMN is_artist        boolean NOT NULL DEFAULT false,
  ADD COLUMN band_url         text CHECK (band_url ~ '^https://[^\s/]+' AND char_length(band_url) <= 200),
  ADD COLUMN instagram_handle text CHECK (instagram_handle ~ '^[A-Za-z0-9._]{1,30}$'),
  ADD COLUMN x_handle         text CHECK (x_handle ~ '^[A-Za-z0-9_]{1,15}$'),
  ADD CONSTRAINT users_artist_links
    CHECK (is_artist OR (band_url IS NULL AND instagram_handle IS NULL AND x_handle IS NULL));

-- The artist flag and links are personal fields: account deletion clears them and they stay empty
-- (same rule as trg_user_delete in 0001 for handle, email, display name, and avatar).
CREATE FUNCTION trg_user_artist_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.deleted_at IS NOT NULL THEN
    IF NEW.is_artist OR NEW.band_url IS NOT NULL OR NEW.instagram_handle IS NOT NULL OR NEW.x_handle IS NOT NULL THEN
      RAISE EXCEPTION 'user % is deleted; personal fields stay empty', OLD.id;
    END IF;
  ELSIF NEW.deleted_at IS NOT NULL THEN
    NEW.is_artist := false;
    NEW.band_url := NULL;
    NEW.instagram_handle := NULL;
    NEW.x_handle := NULL;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER users_artist_scrub BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION trg_user_artist_scrub();
