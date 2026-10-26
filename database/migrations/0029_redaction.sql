-- Redaction and archive (Phase 7, 2026-10-26). A verified submission's files are scanned for barcodes and QR codes,
-- the codes are blacked out, and the redacted copy moves from quarantine/ to archive/ (the quarantine bucket deletes
-- everything after 30 days). What the scanner can't settle goes to staff: a ticket or screenshot with no code found
-- (a ticket nearly always has one, so the scanner probably missed it), codes still found after redacting, or an
-- image that can't be read.

ALTER TABLE submission_files
  ADD COLUMN redaction_state text NOT NULL DEFAULT 'pending'
    CONSTRAINT submission_files_redaction_state_check
    CHECK (redaction_state IN ('pending', 'approved', 'needs_review', 'archived')),
  ADD COLUMN redaction_note text,                -- why staff must look (needs_review), or what was done (archived)
  ADD COLUMN codes_redacted integer CHECK (codes_redacted >= 0);

-- Archived means in the archive tier, under an archive/ key, with a time (and redacted: the original constraint,
-- submission_files_check). Nothing else is under archive/.
ALTER TABLE submission_files
  ADD CONSTRAINT submission_files_archive_consistent CHECK (
    (redaction_state = 'archived') = (tier = 'archive')
    AND (tier = 'archive') = (storage_key LIKE 'archive/%')
    AND (tier <> 'archive' OR archived_at IS NOT NULL));

CREATE INDEX submission_files_redaction_idx ON submission_files (uploaded_at)
  WHERE redaction_state IN ('pending', 'approved', 'needs_review');

-- Becoming verified queues the redaction (the hourly redaction.sweep catches anything this misses).
CREATE FUNCTION trg_submissions_queue_redaction() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM enqueue_job('submissions.redact', jsonb_build_object('submission_id', NEW.id), 'redact:' || NEW.id);
  RETURN NULL;
END $$;
CREATE TRIGGER submissions_queue_redaction AFTER UPDATE OF status ON submissions
  FOR EACH ROW WHEN (NEW.status = 'verified' AND OLD.status IS DISTINCT FROM 'verified')
  EXECUTE FUNCTION trg_submissions_queue_redaction();

-- Staff hear about a file that needs them, once per file.
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction'));

CREATE FUNCTION trg_submission_files_notify_redaction() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM notify_staff('staff_redaction', NEW.id,
                       jsonb_build_object('submission_id', NEW.submission_id, 'note', NEW.redaction_note));
  RETURN NULL;
END $$;
CREATE TRIGGER submission_files_notify_redaction AFTER UPDATE OF redaction_state ON submission_files
  FOR EACH ROW WHEN (NEW.redaction_state = 'needs_review' AND OLD.redaction_state IS DISTINCT FROM 'needs_review')
  EXECUTE FUNCTION trg_submission_files_notify_redaction();
