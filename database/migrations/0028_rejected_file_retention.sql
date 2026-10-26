-- File retention for rejected and taken-down photos (decided 2026-10-25): their stored files are kept 30 days, then
-- storage.cleanup deletes them. The submission row stays (its status, rejection reason, checks, and points), only the
-- files go. A rejected submission's 30 days run from when it was rejected, so that moment is stamped here.

ALTER TABLE submissions ADD COLUMN rejected_at timestamptz;   -- when it last became rejected; null otherwise

-- Submissions already rejected get their 30 days from this migration (when they were rejected isn't recorded).
UPDATE submissions SET rejected_at = now() WHERE status = 'rejected';

CREATE FUNCTION trg_submissions_stamp_rejected() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'rejected' THEN
    IF TG_OP = 'INSERT' OR OLD.status IS DISTINCT FROM 'rejected' THEN
      NEW.rejected_at := now();
    END IF;
  ELSE
    NEW.rejected_at := NULL;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_stamp_rejected BEFORE INSERT OR UPDATE OF status ON submissions
  FOR EACH ROW EXECUTE FUNCTION trg_submissions_stamp_rejected();

-- What storage.cleanup looks for each hour.
CREATE INDEX submissions_rejected_idx ON submissions (rejected_at) WHERE status = 'rejected';
CREATE INDEX submissions_removed_idx ON submissions (removed_at) WHERE removed_at IS NOT NULL;
