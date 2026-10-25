-- Follows and grab bags (Phase 8, decided 2026-10-20). They feed the mixed home feed (GET /feed): followers see a
-- fan's posts, followers-only album logs, grab-bag songs, and top fandom stats.
-- - Follow/follower, not mutual friends: following needs no approval; a fan can remove a follower. Blocking is
--   not wanted for now (decided 2026-10-24).
-- - Grab bag: up to 5 favourite songs per fan, in numbered slots, shared with followers (can't be turned off). Each
--   song links to Spotify and names its artist; adding a song by an untracked artist starts tracking that artist
--   through track_artist(), without counting toward the fan's 50-artists-per-day limit.
-- Account deletion removes the account's follows (both ways) and its grab bag at once: both are personal, like the
-- Spotify connection, not content kept for 30 days.

-- ---------------------------------------------------------------- follows
CREATE TABLE follows (
  follower_id uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  followee_id uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (follower_id, followee_id),
  CONSTRAINT follows_not_self CHECK (follower_id <> followee_id)
);
CREATE INDEX follows_followee_idx ON follows (followee_id, created_at DESC);

-- Both accounts must be live: a deleted account can neither follow nor be followed.
CREATE FUNCTION trg_follow_live_users() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM users WHERE id IN (NEW.follower_id, NEW.followee_id) AND deleted_at IS NOT NULL) THEN
    RAISE EXCEPTION 'follow: deleted accounts cannot follow or be followed';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER follows_live_users BEFORE INSERT OR UPDATE ON follows
  FOR EACH ROW EXECUTE FUNCTION trg_follow_live_users();

-- ---------------------------------------------------------------- grab bags
-- The song's details are kept here (not in tracks): the artist may still be onboarding, so its catalog isn't
-- loaded yet. cover_url is Spotify's album art, hotlinked and unaltered (ADR 0006).
CREATE TABLE grab_bag_songs (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id          uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  position         smallint NOT NULL CHECK (position BETWEEN 1 AND 5),
  spotify_track_id text NOT NULL CHECK (spotify_track_id ~ '^[0-9A-Za-z]{22}$'),
  title            text NOT NULL CHECK (title <> ''),
  artist_id        uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  album_title      text,
  cover_url        text,
  added_at         timestamptz NOT NULL DEFAULT now(),
  UNIQUE (user_id, position),
  UNIQUE (user_id, spotify_track_id)
);
CREATE INDEX grab_bag_songs_user_idx ON grab_bag_songs (user_id, position);

-- Deleting an account removes its follows and its grab bag at once (personal data, as with spotify_connections
-- in trg_user_delete from 0001).
CREATE FUNCTION trg_user_social_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM follows WHERE follower_id = NEW.id OR followee_id = NEW.id;
  DELETE FROM grab_bag_songs WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER users_social_scrub AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_social_scrub();

-- ---------------------------------------------------------------- artists tracked through a grab bag
-- How the artist came to be tracked. 'fan': by hand or from a Spotify sync's top artists (both count toward the
-- daily limit); 'grab_bag': through a grab-bag song (doesn't count, decided 2026-10-20).
ALTER TABLE artists ADD COLUMN added_via text NOT NULL DEFAULT 'fan' CHECK (added_via IN ('fan', 'grab_bag'));

-- Same body as 0007, plus p_via, and only 'fan' adds count toward the limit. The old signature is dropped so a
-- three- or four-argument call resolves to this one (both versions would match through defaults otherwise).
DROP FUNCTION track_artist(uuid, text, text, integer);
CREATE FUNCTION track_artist(p_user uuid, p_spotify_artist_id text, p_name text,
                             p_daily_limit integer DEFAULT 50, p_via text DEFAULT 'fan')
RETURNS TABLE (o_artist_id uuid, o_created boolean) LANGUAGE plpgsql AS $$
DECLARE v_id uuid; v_new boolean; v_recent integer;
BEGIN
  SELECT a.id INTO v_id FROM artists a WHERE a.spotify_artist_id = p_spotify_artist_id;
  IF FOUND THEN
    RETURN QUERY SELECT v_id, false;
    RETURN;
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('track_artist:' || p_user::text, 0));
  IF p_via = 'fan' THEN
    SELECT count(*) INTO v_recent FROM artists a
     WHERE a.added_by = p_user AND a.added_via = 'fan'
       AND a.tracked_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC');
    IF v_recent >= p_daily_limit THEN
      RAISE EXCEPTION 'daily artist add limit reached' USING ERRCODE = 'P0001';
    END IF;
  END IF;

  INSERT INTO artists (name, spotify_artist_id, added_by, added_via)
  VALUES (p_name, p_spotify_artist_id, p_user, p_via)
  ON CONFLICT (spotify_artist_id) DO UPDATE SET name = artists.name   -- no-op, returns existing id
  RETURNING artists.id, (xmax = 0) INTO v_id, v_new;
  IF v_new THEN
    PERFORM request_artist_onboarding(v_id);
  END IF;
  RETURN QUERY SELECT v_id, v_new;
END $$;
