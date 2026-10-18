-- Background job queue (ADR 0004), deleted-account content retention, and the artist add limit.

-- ---------------------------------------------------------------- jobs
-- A small Postgres queue. Jobs are claimed with FOR UPDATE SKIP LOCKED, so any number of worker replicas can
-- share it. Enqueue in the same transaction as the change that needs the work (enqueue_job), so either both
-- happen or neither does. Handlers must be idempotent: a job can run more than once after a crash.
CREATE TABLE jobs (
  id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  kind         text NOT NULL CHECK (kind ~ '^[a-z0-9_.-]+$'),
  payload      jsonb NOT NULL DEFAULT '{}',
  dedupe_key   text,                                -- at most one queued job per key
  status       text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'running', 'succeeded', 'failed')),
  run_at       timestamptz NOT NULL DEFAULT now(),  -- not before; also the retry time
  attempts     integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  max_attempts integer NOT NULL DEFAULT 10 CHECK (max_attempts > 0),
  locked_by    text,
  locked_until timestamptz,                          -- lease; an expired lease means the worker died
  last_error   text,                                 -- exception type and message, never secrets
  created_at   timestamptz NOT NULL DEFAULT now(),
  finished_at  timestamptz,
  CHECK ((status = 'running') = (locked_until IS NOT NULL)),
  CHECK ((status IN ('succeeded', 'failed')) = (finished_at IS NOT NULL))
);
CREATE INDEX jobs_ready_idx   ON jobs (run_at) WHERE status = 'queued';
CREATE INDEX jobs_lease_idx   ON jobs (locked_until) WHERE status = 'running';
CREATE INDEX jobs_finished_idx ON jobs (finished_at) WHERE status IN ('succeeded', 'failed');
CREATE UNIQUE INDEX jobs_dedupe_uidx ON jobs (dedupe_key) WHERE status = 'queued' AND dedupe_key IS NOT NULL;

-- Returns the new job id, or NULL when a queued job with the same dedupe key already exists.
CREATE FUNCTION enqueue_job(p_kind text, p_payload jsonb DEFAULT '{}', p_dedupe_key text DEFAULT NULL,
                            p_run_at timestamptz DEFAULT now(), p_max_attempts integer DEFAULT 10)
RETURNS bigint LANGUAGE sql AS $$
  INSERT INTO jobs (kind, payload, dedupe_key, run_at, max_attempts)
  VALUES (p_kind, p_payload, p_dedupe_key, p_run_at, p_max_attempts)
  ON CONFLICT (dedupe_key) WHERE status = 'queued' AND dedupe_key IS NOT NULL DO NOTHING
  RETURNING id;
$$;

-- Recurring jobs. The worker registers its schedules at startup; fire_due_schedules enqueues each due one
-- exactly once even with several workers.
CREATE TABLE job_schedules (
  kind        text PRIMARY KEY CHECK (kind ~ '^[a-z0-9_.-]+$'),
  every       interval NOT NULL CHECK (every > interval '0'),
  next_run_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION fire_due_schedules() RETURNS integer LANGUAGE sql AS $$
  WITH due AS (
    SELECT kind FROM job_schedules WHERE next_run_at <= now() FOR UPDATE SKIP LOCKED
  ), fired AS (
    UPDATE job_schedules s SET next_run_at = now() + s.every FROM due WHERE s.kind = due.kind RETURNING s.kind
  )
  SELECT count(enqueue_job(kind, '{}', 'schedule:' || kind))::integer FROM fired;
$$;

-- ---------------------------------------------------------------- deleted-account retention
-- Decided 2026-10-18: 30 days after deletion, the account's content is hard-deleted. Comments that others
-- replied to stay as "[deleted]" tombstones so threads still read. Ledger rows stay (reversed), anonymous,
-- for fraud audits.
ALTER TABLE users ADD COLUMN content_purged_at timestamptz,
  ADD CONSTRAINT users_purge_after_delete CHECK (content_purged_at IS NULL OR deleted_at IS NOT NULL);
CREATE INDEX users_purge_due_idx ON users (deleted_at) WHERE deleted_at IS NOT NULL AND content_purged_at IS NULL;

-- Returns false if already purged. Refuses accounts that are live or deleted less than p_retention ago.
CREATE FUNCTION purge_deleted_user(p_user uuid, p_retention interval DEFAULT interval '30 days')
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE v_deleted timestamptz; v_purged timestamptz; v_rows integer;
BEGIN
  SELECT deleted_at, content_purged_at INTO v_deleted, v_purged FROM users WHERE id = p_user FOR UPDATE;
  IF v_deleted IS NULL THEN RAISE EXCEPTION 'user % is not deleted', p_user; END IF;
  IF v_purged IS NOT NULL THEN RETURN false; END IF;
  IF v_deleted > now() - p_retention THEN
    RAISE EXCEPTION 'user % is still within the retention window', p_user;
  END IF;
  -- Stored files must be removed from object storage (and their rows deleted) first, or they would be orphaned.
  IF EXISTS (SELECT 1 FROM submission_files f JOIN submissions s ON s.id = f.submission_id WHERE s.user_id = p_user) THEN
    RAISE EXCEPTION 'user % still has stored submission files; delete them from storage first', p_user;
  END IF;

  -- Keep the ledger but take the account out of every score.
  UPDATE score_events SET reversed_at = now() WHERE user_id = p_user AND reversed_at IS NULL;
  DELETE FROM user_artist_scores WHERE user_id = p_user;
  DELETE FROM share_cards        WHERE user_id = p_user;
  UPDATE outbound_clicks SET user_id = NULL WHERE user_id = p_user;
  DELETE FROM lists              WHERE user_id = p_user;
  DELETE FROM submissions        WHERE user_id = p_user;

  -- Remove comments nobody replied to, repeatedly (a reply's removal can free its parent);
  -- tombstone the rest.
  LOOP
    DELETE FROM comments c WHERE c.user_id = p_user
       AND NOT EXISTS (SELECT 1 FROM comments r WHERE r.parent_id = c.id);
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    EXIT WHEN v_rows = 0;
  END LOOP;
  UPDATE comments SET body = '[deleted]', deleted_at = COALESCE(deleted_at, now()) WHERE user_id = p_user;

  -- Logs last: comments on them (including other people's) go with them.
  DELETE FROM album_logs WHERE user_id = p_user;

  UPDATE users SET content_purged_at = now() WHERE id = p_user;
  RETURN true;
END $$;

-- ---------------------------------------------------------------- artist add limit
-- Decided 2026-10-18: 50 new tracked artists per user per UTC day (was 10). Same body as 0001.
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
  RETURN QUERY SELECT v_id, v_new;
END $$;
