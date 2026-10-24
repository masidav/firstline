-- Reports and staff takedowns (Phase 9). Fans report a shared photo or an album log; staff take it down or dismiss
-- the reports. Every staff decision is kept in moderation_actions.
-- Takedown: a photo leaves the feed and can't be shared again (it stays as proof, points unchanged); a log is hidden
-- from everyone but its author and its points are reversed, as when a log is deleted.

CREATE TABLE reports (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  reporter_id uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  target_type text NOT NULL CHECK (target_type IN ('photo', 'log')),
  target_id   uuid NOT NULL,                       -- submissions.id for a photo, album_logs.id for a log
  reason      text NOT NULL CHECK (reason IN ('spam', 'abuse', 'impersonation', 'inappropriate', 'other')),
  details     text CHECK (char_length(details) <= 1000),
  created_at  timestamptz NOT NULL DEFAULT now(),
  resolved_at timestamptz,
  resolution  text CHECK (resolution IN ('taken_down', 'dismissed')),
  CHECK ((resolved_at IS NULL) = (resolution IS NULL))
);
-- One open report per fan and post: reporting again doesn't pile up.
CREATE UNIQUE INDEX reports_open_uidx ON reports (reporter_id, target_type, target_id) WHERE resolved_at IS NULL;
CREATE INDEX reports_open_target_idx ON reports (target_type, target_id) WHERE resolved_at IS NULL;
CREATE INDEX reports_reporter_idx ON reports (reporter_id, created_at DESC);

-- Audit trail of staff decisions. Append-only, like the ledger.
CREATE TABLE moderation_actions (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  staff_id    uuid REFERENCES users ON DELETE SET NULL,
  target_type text NOT NULL CHECK (target_type IN ('photo', 'log')),
  target_id   uuid NOT NULL,
  action      text NOT NULL CHECK (action IN ('take_down', 'dismiss')),
  note        text CHECK (char_length(note) <= 2000),
  reports     integer NOT NULL CHECK (reports >= 0),  -- open reports this decision resolved
  created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX moderation_actions_target_idx ON moderation_actions (target_type, target_id, created_at);

CREATE FUNCTION trg_moderation_actions_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  -- A deleted staff account nulls staff_id (FK); nothing else may change, and rows are never deleted directly.
  IF TG_OP = 'UPDATE' AND NEW.staff_id IS NULL AND OLD.staff_id IS NOT NULL
     AND (NEW.id, NEW.target_type, NEW.target_id, NEW.action, NEW.note, NEW.reports, NEW.created_at)
         IS NOT DISTINCT FROM (OLD.id, OLD.target_type, OLD.target_id, OLD.action, OLD.note, OLD.reports, OLD.created_at) THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'moderation_actions is append-only';
END $$;
CREATE TRIGGER moderation_actions_append_only BEFORE UPDATE OR DELETE ON moderation_actions
  FOR EACH ROW EXECUTE FUNCTION trg_moderation_actions_append_only();

ALTER TABLE album_logs ADD COLUMN removed_at timestamptz;   -- taken down by staff; only the author still sees it
ALTER TABLE submissions ADD COLUMN removed_at timestamptz;  -- taken off the feed by staff; can't be shared again

-- A taken-down log loses its points, like a deleted one.
CREATE FUNCTION trg_log_removed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM reverse_points('album_log', NEW.id);
  RETURN NEW;
END $$;
CREATE TRIGGER album_logs_removed AFTER UPDATE OF removed_at ON album_logs
  FOR EACH ROW WHEN (OLD.removed_at IS NULL AND NEW.removed_at IS NOT NULL)
  EXECUTE FUNCTION trg_log_removed();

-- A taken-down photo is unshared and stays unshared (runs before the share guard: name order).
CREATE FUNCTION trg_submission_removed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.removed_at IS NOT NULL THEN
    IF OLD.removed_at IS NOT NULL AND NEW.shared_at IS NOT NULL AND OLD.shared_at IS NULL THEN
      RAISE EXCEPTION 'this photo was taken down and can''t be shared again';
    END IF;
    NEW.shared_at := NULL;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_removed BEFORE UPDATE OF removed_at, shared_at ON submissions
  FOR EACH ROW WHEN (NEW.removed_at IS NOT NULL) EXECUTE FUNCTION trg_submission_removed();
