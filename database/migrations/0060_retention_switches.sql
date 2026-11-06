-- Retention switches for records whose period is still an owner decision (privacy review 2026-11-01; PROJECT_TODO
-- "Decide retention for ..."). Nothing here decides anything: every period is NULL until the owner sets it, and a
-- NULL period skips that record. The worker passes its Retention__* settings (unset by default) once a day
-- (privacy.retention, workers/account-retention/PruneExpiredRecordsJob.cs), so deciding later is a configuration
-- change, not a migration.
--
--   p_read_notifications  delete notifications read longer ago than this (proposal: 180 days)
--   p_notifications       delete notifications older than this, read or not (proposal: 1 year)
--   p_click_links         forget who made an outbound click (ticket or merch link) older than this (proposal: 1 year)
--   p_ocr_text            clear the OCR text of a submission decided longer ago than this (proposal: 30 days); the
--                         order-reference hash stays, so duplicate tickets are still caught
CREATE FUNCTION prune_optional_records(p_read_notifications interval DEFAULT NULL,
                                       p_notifications interval DEFAULT NULL,
                                       p_click_links interval DEFAULT NULL,
                                       p_ocr_text interval DEFAULT NULL)
RETURNS TABLE (record text, removed integer) LANGUAGE plpgsql AS $$
DECLARE v integer;
BEGIN
  IF p_read_notifications IS NOT NULL THEN
    DELETE FROM notifications WHERE read_at < now() - p_read_notifications;   -- push and email delivery records cascade
    GET DIAGNOSTICS v = ROW_COUNT; record := 'read_notifications'; removed := v; RETURN NEXT;
  END IF;

  IF p_notifications IS NOT NULL THEN
    DELETE FROM notifications WHERE created_at < now() - p_notifications;
    GET DIAGNOSTICS v = ROW_COUNT; record := 'notifications'; removed := v; RETURN NEXT;
  END IF;

  IF p_click_links IS NOT NULL THEN
    UPDATE outbound_clicks SET user_id = NULL WHERE user_id IS NOT NULL AND clicked_at < now() - p_click_links;
    GET DIAGNOSTICS v = ROW_COUNT; record := 'click_links'; removed := v; RETURN NEXT;
  END IF;

  IF p_ocr_text IS NOT NULL THEN
    UPDATE submissions SET extracted = '{}'
     WHERE extracted <> '{}'
       AND ((status = 'verified' AND verified_at < now() - p_ocr_text) OR (status = 'rejected' AND rejected_at < now() - p_ocr_text));
    GET DIAGNOSTICS v = ROW_COUNT; record := 'ocr_text'; removed := v; RETURN NEXT;
  END IF;
END $$;
