-- Cleaning up stored photos and videos (Phase 7). A stored file is named by a row: uploads.storage_key and, once the
-- upload is attached, submission_files.storage_key (the same key). Deleting those rows (a withdrawn submission, an
-- expired upload slot, a purged account, a cascade) would forget the file, and a crash between deleting the row and
-- deleting the file would orphan it for good. So every removed key is written down here, in the same transaction as
-- the delete, and the worker's storage.cleanup job deletes the file and then this row. No file outlives its rows:
-- until it is gone from storage, a row here still names it.

CREATE TABLE storage_deletions (
  storage_key text PRIMARY KEY CHECK (char_length(storage_key) > 0),
  queued_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX storage_deletions_queued_idx ON storage_deletions (queued_at);

-- A deleted row, or a row whose key changed, queues its old key. Several rows can name one key (an upload and its
-- submission file); it is queued once. The worker skips keys that are still named by a live row.
CREATE FUNCTION trg_queue_storage_deletion() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO storage_deletions (storage_key) VALUES (OLD.storage_key) ON CONFLICT (storage_key) DO NOTHING;
  RETURN NULL;
END $$;

CREATE TRIGGER uploads_queue_storage_deletion AFTER DELETE ON uploads
  FOR EACH ROW WHEN (OLD.storage_key IS NOT NULL) EXECUTE FUNCTION trg_queue_storage_deletion();
CREATE TRIGGER uploads_queue_storage_rekey AFTER UPDATE OF storage_key ON uploads
  FOR EACH ROW WHEN (OLD.storage_key IS NOT NULL AND OLD.storage_key IS DISTINCT FROM NEW.storage_key)
  EXECUTE FUNCTION trg_queue_storage_deletion();
CREATE TRIGGER submission_files_queue_storage_deletion AFTER DELETE ON submission_files
  FOR EACH ROW EXECUTE FUNCTION trg_queue_storage_deletion();
CREATE TRIGGER submission_files_queue_storage_rekey AFTER UPDATE OF storage_key ON submission_files
  FOR EACH ROW WHEN (OLD.storage_key IS DISTINCT FROM NEW.storage_key)
  EXECUTE FUNCTION trg_queue_storage_deletion();

-- Unattached slots by user, for removing a deleted account's leftovers.
CREATE INDEX uploads_user_unattached_idx ON uploads (user_id) WHERE submission_id IS NULL;
