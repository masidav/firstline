-- Fandom score app: Postgres schema (Postgres 14+)
-- Sections: extensions/enums, identity, catalog, Spotify signals, journal,
-- submissions + verification, scoring ledger, outputs, triggers, views, seed rules.
--
-- Scope rule: only artists on the tracked list (the `artists` table) are synced, scored,
-- journaled, or verified. Users add artists by searching; see track_artist() below.

CREATE EXTENSION IF NOT EXISTS citext;

-- ---------------------------------------------------------------- enums
CREATE TYPE submission_kind   AS ENUM ('ticket', 'merch', 'screenshot');
CREATE TYPE submission_status AS ENUM ('pending', 'processing', 'needs_review', 'verified', 'rejected');
CREATE TYPE check_type        AS ENUM ('file_hash', 'metadata', 'ocr_extract', 'event_match',
                                       'order_ref', 'partner_confirm', 'manual');
CREATE TYPE check_result      AS ENUM ('pass', 'fail', 'inconclusive');
CREATE TYPE storage_tier      AS ENUM ('hot', 'archive');
CREATE TYPE visibility        AS ENUM ('public', 'followers', 'private');
CREATE TYPE outbound_kind     AS ENUM ('ticket', 'merch', 'website');
-- onboarding: row exists, catalog + baseline being prepared. active: synced and scored.
-- paused: stops syncing and awarding points (e.g. takedown request).
CREATE TYPE artist_status     AS ENUM ('onboarding', 'active', 'paused');

-- ---------------------------------------------------------------- identity
CREATE TABLE users (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  handle       citext UNIQUE,                    -- NULL only after deletion (freed for reuse)
  email        citext UNIQUE,                    -- NULL only after deletion
  display_name text,
  avatar_url   text,
  is_staff     boolean NOT NULL DEFAULT false,   -- may act as manual reviewer
  created_at   timestamptz NOT NULL DEFAULT now(),
  deleted_at   timestamptz,
  CHECK (deleted_at IS NOT NULL OR (handle IS NOT NULL AND email IS NOT NULL))
);

CREATE TABLE spotify_connections (
  user_id           uuid PRIMARY KEY REFERENCES users ON DELETE CASCADE,
  spotify_user_id   text NOT NULL UNIQUE,
  refresh_token_enc bytea NOT NULL,              -- encrypt app-side (KMS), never store plaintext
  scopes            text[] NOT NULL,
  connected_at      timestamptz NOT NULL DEFAULT now(),
  last_synced_at    timestamptz
);

-- ---------------------------------------------------------------- catalog
-- The tracked-artist list. Seeded by staff (added_by NULL) or created when a user searches
-- for an artist and adds it. Search results for untracked artists come from Spotify and are
-- NOT stored here until someone adds them.
CREATE TABLE artists (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name              text NOT NULL,
  spotify_artist_id text NOT NULL UNIQUE,
  musicbrainz_id    uuid UNIQUE,
  website_url       text,
  merch_url         text,
  status            artist_status NOT NULL DEFAULT 'onboarding',
  added_by          uuid REFERENCES users ON DELETE SET NULL,
  tracked_at        timestamptz NOT NULL DEFAULT now(),
  activated_at      timestamptz,
  CHECK (status <> 'active' OR activated_at IS NOT NULL)
);
CREATE INDEX artists_added_by_idx ON artists (added_by, tracked_at);

-- Synthetic prior for percentiles. curve = precomputed [[score, cdf], ...] points;
-- dist_params lets a worker regenerate it. Real data takes over via w = n / (n + blend_k).
CREATE TABLE artist_baselines (
  artist_id     uuid PRIMARY KEY REFERENCES artists ON DELETE CASCADE,
  dist_family   text NOT NULL DEFAULT 'lognormal',
  dist_params   jsonb NOT NULL,
  proxy_metrics jsonb NOT NULL DEFAULT '{}',     -- followers, popularity, catalog size at seed time
  curve         jsonb NOT NULL,
  blend_k       integer NOT NULL DEFAULT 200 CHECK (blend_k > 0),
  updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE albums (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  artist_id        uuid NOT NULL REFERENCES artists ON DELETE CASCADE,  -- primary artist only
  title            text NOT NULL,
  release_date     date,
  spotify_album_id text UNIQUE,
  musicbrainz_id   uuid UNIQUE,
  cover_url        text,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX albums_artist_idx ON albums (artist_id);

CREATE TABLE tracks (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  album_id         uuid NOT NULL REFERENCES albums ON DELETE CASCADE,
  title            text NOT NULL,
  track_number     smallint,
  popularity       smallint CHECK (popularity BETWEEN 0 AND 100),  -- low = deep cut
  spotify_track_id text UNIQUE
);
CREATE INDEX tracks_album_idx ON tracks (album_id);

CREATE TABLE events (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  artist_id       uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  name            text NOT NULL,
  venue           text,
  city            text,
  country         text,
  starts_at       timestamptz NOT NULL,
  external_source text,
  external_id     text,
  UNIQUE (external_source, external_id)
);
CREATE INDEX events_artist_date_idx ON events (artist_id, starts_at);

-- ---------------------------------------------------------------- Spotify signals
CREATE TABLE user_track_likes (
  user_id  uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  track_id uuid NOT NULL REFERENCES tracks ON DELETE CASCADE,
  source   text NOT NULL CHECK (source IN ('liked', 'playlist')),
  saved_at timestamptz,
  PRIMARY KEY (user_id, track_id, source)
);

-- Aggregated per user and artist by the sync worker. State, not events,
-- so it lives outside the points ledger.
CREATE TABLE user_artist_signals (
  user_id          uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id        uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  liked_tracks     integer NOT NULL DEFAULT 0,
  playlist_tracks  integer NOT NULL DEFAULT 0,
  distinct_albums  integer NOT NULL DEFAULT 0,   -- catalog depth
  top_artist_rank  smallint,
  recent_plays     integer NOT NULL DEFAULT 0,
  synced_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, artist_id)
);

-- ---------------------------------------------------------------- album journal
CREATE TABLE album_logs (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  album_id    uuid NOT NULL REFERENCES albums ON DELETE CASCADE,
  rating      smallint CHECK (rating BETWEEN 1 AND 10),      -- half-stars: 1 = 0.5, 10 = 5.0
  review      text CHECK (char_length(review) <= 10000),
  listened_on date NOT NULL DEFAULT current_date,
  is_relisten boolean NOT NULL DEFAULT false,
  visibility  visibility NOT NULL DEFAULT 'public',
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  deleted_at  timestamptz
);
CREATE INDEX album_logs_album_idx ON album_logs (album_id, created_at DESC);
CREATE INDEX album_logs_user_idx  ON album_logs (user_id, listened_on DESC);

CREATE TABLE lists (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  title       text NOT NULL,
  description text,
  visibility  visibility NOT NULL DEFAULT 'public',
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE list_items (
  list_id  uuid NOT NULL REFERENCES lists ON DELETE CASCADE,
  album_id uuid NOT NULL REFERENCES albums ON DELETE CASCADE,
  position integer NOT NULL,
  note     text,
  PRIMARY KEY (list_id, album_id),
  UNIQUE (list_id, position) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE comments (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  album_log_id uuid NOT NULL REFERENCES album_logs ON DELETE CASCADE,
  user_id      uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  parent_id    uuid,
  body         text NOT NULL CHECK (char_length(body) BETWEEN 1 AND 2000),
  created_at   timestamptz NOT NULL DEFAULT now(),
  edited_at    timestamptz,
  deleted_at   timestamptz,
  UNIQUE (id, album_log_id),
  -- A reply must belong to the same log as its parent.
  FOREIGN KEY (parent_id, album_log_id) REFERENCES comments (id, album_log_id) ON DELETE CASCADE
);
CREATE INDEX comments_log_idx ON comments (album_log_id, created_at);

-- ---------------------------------------------------------------- submissions + verification
CREATE TABLE submissions (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id          uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id        uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  event_id         uuid REFERENCES events,                   -- set when event_match passes
  kind             submission_kind NOT NULL,
  status           submission_status NOT NULL DEFAULT 'pending',
  extracted        jsonb NOT NULL DEFAULT '{}',              -- OCR output (redacted)
  order_ref_hash   bytea,                                    -- sha256 of normalized ticket/order id
  trust_score      numeric(4,3) CHECK (trust_score BETWEEN 0 AND 1),
  rejection_reason text,
  submitted_at     timestamptz NOT NULL DEFAULT now(),
  verified_at      timestamptz,
  archived_at      timestamptz,
  CHECK (status <> 'verified' OR verified_at IS NOT NULL),
  CHECK (archived_at IS NULL OR verified_at IS NOT NULL)
);
CREATE INDEX submissions_user_idx   ON submissions (user_id, submitted_at DESC);
CREATE INDEX submissions_review_idx ON submissions (submitted_at) WHERE status = 'needs_review';
-- One real ticket or order can only ever be verified once.
CREATE UNIQUE INDEX submissions_order_ref_uidx
  ON submissions (kind, order_ref_hash)
  WHERE status = 'verified' AND order_ref_hash IS NOT NULL;

CREATE TABLE submission_files (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  submission_id uuid NOT NULL REFERENCES submissions ON DELETE CASCADE,
  storage_key   text NOT NULL UNIQUE,
  sha256        bytea NOT NULL,
  mime_type     text NOT NULL,
  bytes         bigint NOT NULL,
  tier          storage_tier NOT NULL DEFAULT 'hot',
  redacted      boolean NOT NULL DEFAULT false,              -- barcodes/QR removed before archive
  uploaded_at   timestamptz NOT NULL DEFAULT now(),
  archived_at   timestamptz,
  CHECK (tier <> 'archive' OR redacted)                      -- never archive an unredacted file
);
CREATE INDEX submission_files_sha_idx ON submission_files (sha256);  -- duplicate detection

-- Append-only audit trail: one row per automated or human check.
CREATE TABLE verification_checks (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  submission_id uuid NOT NULL REFERENCES submissions ON DELETE CASCADE,
  check_type    check_type NOT NULL,
  result        check_result NOT NULL,
  confidence    numeric(4,3) CHECK (confidence BETWEEN 0 AND 1),
  details       jsonb NOT NULL DEFAULT '{}',
  reviewer_id   uuid REFERENCES users,
  created_at    timestamptz NOT NULL DEFAULT now(),
  CHECK (check_type <> 'manual' OR reviewer_id IS NOT NULL)
);
CREATE INDEX verification_checks_sub_idx ON verification_checks (submission_id, created_at);

-- ---------------------------------------------------------------- scoring
-- Weights live in data so they can be tuned without a deploy.
-- spotify_* rules are per-unit multipliers used by v_raw_scores; the rest are ledger awards.
CREATE TABLE scoring_rules (
  rule_key         text PRIMARY KEY,
  points           numeric(8,2) NOT NULL,
  daily_cap_count  integer CHECK (daily_cap_count > 0),     -- max awards per user per UTC day
  active           boolean NOT NULL DEFAULT true,
  description      text
);

-- Append-only points ledger. Reverse by setting reversed_at, never delete.
-- trg_score_events_guard enforces this; a reversed source may be awarded again with a new row.
CREATE TABLE score_events (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id   uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  rule_key    text NOT NULL REFERENCES scoring_rules,
  points      numeric(8,2) NOT NULL,
  source_type text NOT NULL CHECK (source_type IN ('album_log', 'comment', 'submission')),
  source_id   uuid NOT NULL,
  awarded_at  timestamptz NOT NULL DEFAULT now(),
  reversed_at timestamptz
);
-- No double awards: at most one unreversed award per rule and source.
CREATE UNIQUE INDEX score_events_source_uidx
  ON score_events (rule_key, source_type, source_id) WHERE reversed_at IS NULL;
CREATE INDEX score_events_user_artist_idx
  ON score_events (user_id, artist_id) WHERE reversed_at IS NULL;

-- Output of the scoring worker (written after blending real and synthetic curves).
CREATE TABLE user_artist_scores (
  user_id         uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id       uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  raw_score       numeric(10,2) NOT NULL,
  percentile      numeric(5,2) NOT NULL CHECK (percentile BETWEEN 0 AND 100),
  is_estimated    boolean NOT NULL DEFAULT true,             -- true while baseline weight is high
  baseline_weight numeric(4,3) NOT NULL,
  breakdown       jsonb NOT NULL DEFAULT '{}',
  computed_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, artist_id)
);
CREATE INDEX user_artist_scores_rank_idx ON user_artist_scores (artist_id, raw_score);

-- ---------------------------------------------------------------- outputs
CREATE TABLE share_cards (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id    uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id  uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  raw_score  numeric(10,2) NOT NULL,
  percentile numeric(5,2) NOT NULL,
  image_url  text NOT NULL,
  shared_to  text[] NOT NULL DEFAULT '{}',                   -- e.g. {x, instagram}
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE outbound_clicks (
  id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id    uuid REFERENCES users ON DELETE SET NULL,
  artist_id  uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  event_id   uuid REFERENCES events,
  kind       outbound_kind NOT NULL,
  partner    text,
  clicked_at timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- functions + triggers
CREATE FUNCTION set_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN NEW.updated_at := now(); RETURN NEW; END $$;

CREATE TRIGGER album_logs_updated BEFORE UPDATE ON album_logs
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Account deletion: scrub personal fields so the handle and email can be reused, and drop the
-- Spotify connection and synced library data (openapi deleteMe). The row stays so the user's
-- content and ledger history keep their owner. Deletion is one-way.
CREATE FUNCTION trg_user_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.deleted_at IS NOT NULL THEN
    IF NEW.deleted_at IS NULL THEN RAISE EXCEPTION 'user % is deleted and cannot be restored', OLD.id; END IF;
    IF (NEW.handle, NEW.email, NEW.display_name, NEW.avatar_url)
       IS DISTINCT FROM (NULL::citext, NULL::citext, NULL::text, NULL::text) THEN
      RAISE EXCEPTION 'user % is deleted; personal fields stay empty', OLD.id;
    END IF;
  ELSIF NEW.deleted_at IS NOT NULL THEN
    NEW.handle := NULL;
    NEW.email := NULL;
    NEW.display_name := NULL;
    NEW.avatar_url := NULL;
    NEW.is_staff := false;
    DELETE FROM spotify_connections WHERE user_id = NEW.id;
    DELETE FROM user_track_likes    WHERE user_id = NEW.id;
    DELETE FROM user_artist_signals WHERE user_id = NEW.id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER users_delete BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION trg_user_delete();

-- Award points for one source. Returns true if a new award was written.
-- Enforces the rule's daily cap per user (UTC day) and idempotency per source.
CREATE FUNCTION award_points(p_user uuid, p_artist uuid, p_rule text,
                             p_source_type text, p_source_id uuid)
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
  r scoring_rules%ROWTYPE;
  todays integer;
  inserted integer;
BEGIN
  SELECT * INTO r FROM scoring_rules WHERE rule_key = p_rule AND active;
  IF NOT FOUND THEN RETURN false; END IF;
  IF NOT EXISTS (SELECT 1 FROM artists WHERE id = p_artist AND status = 'active') THEN
    RETURN false;                                  -- onboarding and paused artists earn nothing
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended(p_user::text || p_rule, 0));

  IF r.daily_cap_count IS NOT NULL THEN
    SELECT count(*) INTO todays FROM score_events
     WHERE user_id = p_user AND rule_key = p_rule AND reversed_at IS NULL
       AND awarded_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC');
    IF todays >= r.daily_cap_count THEN RETURN false; END IF;
  END IF;

  INSERT INTO score_events (user_id, artist_id, rule_key, points, source_type, source_id)
  VALUES (p_user, p_artist, p_rule, r.points, p_source_type, p_source_id)
  ON CONFLICT (rule_key, source_type, source_id) WHERE reversed_at IS NULL DO NOTHING;
  GET DIAGNOSTICS inserted = ROW_COUNT;
  RETURN inserted > 0;
END $$;

-- Reverse every unreversed award for one source (optionally one rule).
CREATE FUNCTION reverse_points(p_source_type text, p_source_id uuid, p_rule text DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
  UPDATE score_events SET reversed_at = now()
   WHERE source_type = p_source_type AND source_id = p_source_id AND reversed_at IS NULL
     AND (p_rule IS NULL OR rule_key = p_rule);
$$;

-- Ledger rows are append-only: the only allowed change is stamping reversed_at once.
-- Deletes are allowed only as a cascade from a deleted user or artist (trigger depth > 1).
CREATE FUNCTION trg_score_events_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
    IF TG_OP = 'DELETE' AND pg_trigger_depth() > 1 THEN RETURN OLD; END IF;
    RAISE EXCEPTION 'score_events is append-only; reverse instead of deleting';
  END IF;
  IF OLD.reversed_at IS NOT NULL OR NEW.reversed_at IS NULL
     OR (NEW.id, NEW.user_id, NEW.artist_id, NEW.rule_key, NEW.points,
         NEW.source_type, NEW.source_id, NEW.awarded_at)
        IS DISTINCT FROM
        (OLD.id, OLD.user_id, OLD.artist_id, OLD.rule_key, OLD.points,
         OLD.source_type, OLD.source_id, OLD.awarded_at) THEN
    RAISE EXCEPTION 'score_events is append-only; only reversed_at may be set, once';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER score_events_guard BEFORE UPDATE OR DELETE ON score_events
  FOR EACH ROW EXECUTE FUNCTION trg_score_events_guard();
CREATE TRIGGER score_events_no_truncate BEFORE TRUNCATE ON score_events
  FOR EACH STATEMENT EXECUTE FUNCTION trg_score_events_guard();

-- Comments: reward only substantive comments on someone else's log.
CREATE FUNCTION trg_comment_award() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_owner uuid; v_artist uuid;
BEGIN
  SELECT l.user_id, a.artist_id INTO v_owner, v_artist
    FROM album_logs l JOIN albums a ON a.id = l.album_id
   WHERE l.id = NEW.album_log_id;
  IF v_owner IS DISTINCT FROM NEW.user_id AND char_length(NEW.body) >= 20 THEN
    PERFORM award_points(NEW.user_id, v_artist, 'comment_posted', 'comment', NEW.id);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER comments_award AFTER INSERT ON comments
  FOR EACH ROW EXECUTE FUNCTION trg_comment_award();

CREATE FUNCTION trg_comment_reverse() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
    PERFORM reverse_points('comment', NEW.id);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER comments_reverse AFTER UPDATE OF deleted_at ON comments
  FOR EACH ROW EXECUTE FUNCTION trg_comment_reverse();

-- Album logs: award on first log (and relisten / review bonuses).
CREATE FUNCTION trg_log_award() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_artist uuid;
BEGIN
  SELECT artist_id INTO v_artist FROM albums WHERE id = NEW.album_id;
  PERFORM award_points(NEW.user_id, v_artist,
                       CASE WHEN NEW.is_relisten THEN 'album_relisten' ELSE 'album_logged' END,
                       'album_log', NEW.id);
  IF NEW.review IS NOT NULL AND char_length(NEW.review) >= 100 THEN
    PERFORM award_points(NEW.user_id, v_artist, 'review_written', 'album_log', NEW.id);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER album_logs_award AFTER INSERT ON album_logs
  FOR EACH ROW EXECUTE FUNCTION trg_log_award();

-- Album logs: soft delete reverses all awards; editing the review awards or reverses
-- review_written so the ledger matches the "100+ characters" rule.
CREATE FUNCTION trg_log_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_artist uuid;
BEGIN
  IF NEW.deleted_at IS NOT NULL THEN
    IF OLD.deleted_at IS NULL THEN PERFORM reverse_points('album_log', NEW.id); END IF;
    RETURN NEW;
  END IF;
  IF NEW.review IS NOT NULL AND char_length(NEW.review) >= 100 THEN
    SELECT artist_id INTO v_artist FROM albums WHERE id = NEW.album_id;
    PERFORM award_points(NEW.user_id, v_artist, 'review_written', 'album_log', NEW.id);
  ELSE
    PERFORM reverse_points('album_log', NEW.id, 'review_written');
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER album_logs_change AFTER UPDATE OF review, deleted_at ON album_logs
  FOR EACH ROW EXECUTE FUNCTION trg_log_change();

-- Submissions: stamp verified_at, award on verify, reverse if later rejected.
CREATE FUNCTION trg_submission_stamp() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'verified' AND NEW.verified_at IS NULL THEN NEW.verified_at := now(); END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_stamp BEFORE UPDATE ON submissions
  FOR EACH ROW EXECUTE FUNCTION trg_submission_stamp();

CREATE FUNCTION trg_submission_award() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'verified' AND OLD.status IS DISTINCT FROM 'verified' THEN
    PERFORM award_points(NEW.user_id, NEW.artist_id, NEW.kind::text || '_verified',
                         'submission', NEW.id);
  ELSIF NEW.status = 'rejected' AND OLD.status = 'verified' THEN
    PERFORM reverse_points('submission', NEW.id);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_award AFTER UPDATE OF status ON submissions
  FOR EACH ROW EXECUTE FUNCTION trg_submission_award();

-- Artists: an artist can only go active once it has a baseline; stamps activated_at.
CREATE FUNCTION trg_artist_activate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'active' AND (TG_OP = 'INSERT' OR OLD.status IS DISTINCT FROM 'active') THEN
    IF NOT EXISTS (SELECT 1 FROM artist_baselines WHERE artist_id = NEW.id) THEN
      RAISE EXCEPTION 'artist % needs a baseline before activation', NEW.id;
    END IF;
    NEW.activated_at := COALESCE(NEW.activated_at, now());
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER artists_activate BEFORE INSERT OR UPDATE OF status ON artists
  FOR EACH ROW EXECUTE FUNCTION trg_artist_activate();

-- Add an artist to the tracked list from a search result.
-- Call from the API server with data fetched from Spotify (never trust client-supplied names).
-- Existing artists return as-is (o_created = false). New ones start in 'onboarding'; a worker
-- then loads the catalog, seeds the baseline, and sets status = 'active'.
-- Limits how many NEW artists one user can add per UTC day.
CREATE FUNCTION track_artist(p_user uuid, p_spotify_artist_id text, p_name text,
                             p_daily_limit integer DEFAULT 10)
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
  RETURN QUERY SELECT v_id, v_new;
END $$;

-- ---------------------------------------------------------------- views
-- Simple raw score: Spotify state * per-unit weights + ledger points. Active artists only.
-- The worker can apply diminishing returns (e.g. log) to Spotify counts before blending.
CREATE VIEW v_raw_scores AS
WITH w AS (
  SELECT max(points) FILTER (WHERE rule_key = 'spotify_liked_track')    AS liked,
         max(points) FILTER (WHERE rule_key = 'spotify_playlist_track') AS playlist
    FROM scoring_rules WHERE active
), led AS (
  SELECT user_id, artist_id, sum(points) AS pts
    FROM score_events WHERE reversed_at IS NULL
   GROUP BY user_id, artist_id
)
SELECT COALESCE(s.user_id, l.user_id)     AS user_id,
       COALESCE(s.artist_id, l.artist_id) AS artist_id,
       COALESCE(s.liked_tracks, 0)    * COALESCE(w.liked, 0)
     + COALESCE(s.playlist_tracks, 0) * COALESCE(w.playlist, 0)
     + COALESCE(l.pts, 0)               AS raw_score
  FROM user_artist_signals s
  FULL JOIN led l ON l.user_id = s.user_id AND l.artist_id = s.artist_id
  CROSS JOIN w
  JOIN artists a ON a.id = COALESCE(s.artist_id, l.artist_id) AND a.status = 'active';

-- ---------------------------------------------------------------- seed rules (tune freely)
INSERT INTO scoring_rules (rule_key, points, daily_cap_count, description) VALUES
  ('spotify_liked_track',    0.50, NULL, 'Per liked track by the artist (per-unit, not a ledger award)'),
  ('spotify_playlist_track', 0.25, NULL, 'Per playlist track by the artist (per-unit, not a ledger award)'),
  ('ticket_verified',       50.00, NULL, 'Verified concert ticket'),
  ('merch_verified',        20.00, NULL, 'Verified merch purchase'),
  ('screenshot_verified',    5.00, NULL, 'Verified fan screenshot'),
  ('album_logged',           3.00, 20,   'First log of an album'),
  ('album_relisten',         1.00, 10,   'Relisten log'),
  ('review_written',         4.00, 10,   'Review of 100+ characters'),
  ('comment_posted',         1.00, 5,    'Comment of 20+ characters on another user''s log');
