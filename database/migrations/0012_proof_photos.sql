-- Proof-of-fandom photos (Phase 7) and sharing them to the home feed.
-- Fans upload a photo (POST /uploads, then PUT the bytes to the signed URL), submit it as proof, and staff review it.
-- A verified merch or concert photo can then be shared; tickets and screenshots never are (barcodes, personal
-- details, and nothing redacts them yet). Files stay in quarantine storage: nothing is archived, so no unredacted
-- file is ever archived.

-- A photo taken at the show. Earns points once staff verify it (rule concert_photo_verified, below).
ALTER TYPE submission_kind ADD VALUE IF NOT EXISTS 'concert_photo';

INSERT INTO scoring_rules (rule_key, points, description) VALUES
  ('concert_photo_verified', 5.00, 'Verified photo from a concert (provisional points, 2026-10-21)');

-- An upload slot: created by POST /uploads, filled by the signed PUT, then attached to one submission.
CREATE TABLE uploads (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id         uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  purpose         text NOT NULL CHECK (purpose IN ('submission')),
  mime_type       text NOT NULL CHECK (mime_type IN ('image/jpeg', 'image/png')),
  declared_bytes  integer NOT NULL CHECK (declared_bytes BETWEEN 1 AND 15728640),
  declared_sha256 bytea CHECK (octet_length(declared_sha256) = 32),
  storage_key     text UNIQUE,                       -- set once the bytes are stored (metadata stripped)
  sha256          bytea CHECK (octet_length(sha256) = 32),
  bytes           integer CHECK (bytes > 0),
  created_at      timestamptz NOT NULL DEFAULT now(),
  expires_at      timestamptz NOT NULL DEFAULT now() + interval '1 hour',
  uploaded_at     timestamptz,
  submission_id   uuid REFERENCES submissions ON DELETE CASCADE,
  CHECK ((uploaded_at IS NULL) = (storage_key IS NULL)),
  CHECK ((storage_key IS NULL) = (sha256 IS NULL) AND (storage_key IS NULL) = (bytes IS NULL)),
  CHECK (submission_id IS NULL OR uploaded_at IS NOT NULL)
);
CREATE INDEX uploads_user_idx ON uploads (user_id, created_at DESC);
CREATE INDEX uploads_unattached_idx ON uploads (expires_at) WHERE submission_id IS NULL;

ALTER TABLE submissions
  ADD COLUMN caption       text CHECK (char_length(caption) <= 500),
  ADD COLUMN shared_at     timestamptz,          -- on the home feed since; null when not shared
  -- The concert a photo is from, typed in by the fan when the show isn't in events (no concert data source yet).
  ADD COLUMN concert_date  date,
  ADD COLUMN concert_venue text CHECK (char_length(concert_venue) BETWEEN 1 AND 200),
  ADD COLUMN concert_city  text CHECK (char_length(concert_city) BETWEEN 1 AND 100),
  ADD CONSTRAINT submissions_concert_fields
    CHECK ((concert_date IS NULL) = (concert_venue IS NULL) AND (concert_venue IS NULL) = (concert_city IS NULL)),
  -- A concert photo is pinned to its band (artist_id) and its concert: a listed event, or the fan's date, venue, city.
  ADD CONSTRAINT submissions_concert_pinned
    CHECK (kind::text <> 'concert_photo' OR event_id IS NOT NULL OR concert_date IS NOT NULL);

-- The pinned event must be the same band's show.
CREATE FUNCTION trg_submission_event_artist() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM events WHERE id = NEW.event_id AND artist_id = NEW.artist_id) THEN
    RAISE EXCEPTION 'the event is not a show by this artist';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_event_artist BEFORE INSERT OR UPDATE OF event_id, artist_id ON submissions
  FOR EACH ROW WHEN (NEW.event_id IS NOT NULL) EXECUTE FUNCTION trg_submission_event_artist();

-- Leaving verified (a later rejection) takes the photo off the feed. BEFORE triggers run in name order, so this
-- one ("clear") runs before the share guard below.
CREATE FUNCTION trg_submission_unshare() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.shared_at := NULL;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_clear_share BEFORE UPDATE OF status ON submissions
  FOR EACH ROW WHEN (OLD.status = 'verified' AND NEW.status <> 'verified')
  EXECUTE FUNCTION trg_submission_unshare();

-- Only verified merch and concert photos can be shared.
CREATE FUNCTION trg_submission_share_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.shared_at IS NOT NULL THEN
    IF NEW.kind::text NOT IN ('merch', 'concert_photo') THEN
      RAISE EXCEPTION 'only merch and concert photos can be shared';
    END IF;
    IF NEW.status <> 'verified' THEN
      RAISE EXCEPTION 'only verified submissions can be shared';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_share_guard BEFORE INSERT OR UPDATE OF shared_at, status, kind ON submissions
  FOR EACH ROW WHEN (NEW.shared_at IS NOT NULL)
  EXECUTE FUNCTION trg_submission_share_guard();


CREATE INDEX submissions_shared_idx ON submissions (shared_at DESC) WHERE shared_at IS NOT NULL;
CREATE INDEX album_logs_public_idx ON album_logs (created_at DESC) WHERE deleted_at IS NULL AND visibility = 'public';
