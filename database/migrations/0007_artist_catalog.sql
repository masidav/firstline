-- Album catalog loaded from Spotify during artist onboarding and refreshed weekly (ADR 0006).
-- Removed releases are delisted, never deleted: deleting an album would cascade-delete fans' logs.

ALTER TABLE albums
  ADD COLUMN album_type text NOT NULL DEFAULT 'album'
    CONSTRAINT albums_album_type CHECK (album_type IN ('album', 'single', 'compilation')),
  -- Spotify gives some release dates as a year or a month; release_date then holds the first day.
  ADD COLUMN release_date_precision text NOT NULL DEFAULT 'day'
    CONSTRAINT albums_release_date_precision CHECK (release_date_precision IN ('year', 'month', 'day')),
  ADD COLUMN delisted_at timestamptz,                 -- Spotify no longer lists it for the artist
  ADD COLUMN refreshed_at timestamptz;                -- last load from Spotify; null for rows never loaded
CREATE INDEX albums_live_idx ON albums (artist_id, release_date DESC NULLS LAST) WHERE delisted_at IS NULL;

ALTER TABLE tracks
  ADD COLUMN disc_number smallint NOT NULL DEFAULT 1 CHECK (disc_number >= 1),
  ADD COLUMN delisted_at timestamptz;

-- Last successful catalog load; the refresh schedule and the activation guard read it.
ALTER TABLE artists ADD COLUMN catalog_loaded_at timestamptz;
CREATE INDEX artists_catalog_due_idx ON artists (catalog_loaded_at NULLS FIRST) WHERE status = 'active';

-- Activation now also needs the catalog (same trigger, one more check).
CREATE OR REPLACE FUNCTION trg_artist_activate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'active' AND (TG_OP = 'INSERT' OR OLD.status IS DISTINCT FROM 'active') THEN
    IF NOT EXISTS (SELECT 1 FROM artist_baselines WHERE artist_id = NEW.id) THEN
      RAISE EXCEPTION 'artist % needs a baseline before activation', NEW.id;
    END IF;
    IF NEW.catalog_loaded_at IS NULL THEN
      RAISE EXCEPTION 'artist % needs its catalog before activation', NEW.id;
    END IF;
    NEW.activated_at := COALESCE(NEW.activated_at, now());
  END IF;
  RETURN NEW;
END $$;

-- Queues the onboarding job (catalog, baseline, activation) for an artist still onboarding, at most one
-- queued per artist. Returns false for active or paused artists. Staff can call it to retry a failed one.
CREATE FUNCTION request_artist_onboarding(p_artist uuid) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM artists WHERE id = p_artist AND status = 'onboarding') THEN
    RETURN false;
  END IF;
  PERFORM enqueue_job('artist.onboard', jsonb_build_object('artist_id', p_artist), 'artist.onboard:' || p_artist);
  RETURN true;
END $$;

-- track_artist: same body as 0004, plus the onboarding job in the same transaction as the new row.
CREATE OR REPLACE FUNCTION track_artist(p_user uuid, p_spotify_artist_id text, p_name text,
                                        p_daily_limit integer DEFAULT 50)
RETURNS TABLE (o_artist_id uuid, o_created boolean) LANGUAGE plpgsql AS $$
DECLARE v_id uuid; v_new boolean; v_recent integer;
BEGIN
  SELECT a.id INTO v_id FROM artists a WHERE a.spotify_artist_id = p_spotify_artist_id;
  IF FOUND THEN
    RETURN QUERY SELECT v_id, false;
    RETURN;
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('track_artist:' || p_user::text, 0));
  SELECT count(*) INTO v_recent FROM artists a
   WHERE a.added_by = p_user
     AND a.tracked_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC');
  IF v_recent >= p_daily_limit THEN
    RAISE EXCEPTION 'daily artist add limit reached' USING ERRCODE = 'P0001';
  END IF;

  INSERT INTO artists (name, spotify_artist_id, added_by)
  VALUES (p_name, p_spotify_artist_id, p_user)
  ON CONFLICT (spotify_artist_id) DO UPDATE SET name = artists.name   -- no-op, returns existing id
  RETURNING artists.id, (xmax = 0) INTO v_id, v_new;
  IF v_new THEN
    PERFORM request_artist_onboarding(v_id);
  END IF;
  RETURN QUERY SELECT v_id, v_new;
END $$;

-- Artists added before this migration are still waiting: queue their onboarding now (idempotent via dedupe key).
SELECT request_artist_onboarding(id) FROM artists WHERE status = 'onboarding';
