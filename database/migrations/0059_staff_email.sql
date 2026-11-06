-- Staff notices by email (Phase 9, 2026-11-06). Every staff event that writes an in-app notice (the staff_* kinds,
-- migrations 0021, 0029, 0036, 0042) also queues an email to the staff member's account address, unless they turned
-- it off. The worker sends it through whichever sender is configured (workers/notifications/email); with none, the
-- job does nothing and the notice stays in-app only. Fans never get email from this (no fan kind queues one).

-- "Staff notices by email", on by default like push_staff (0032). Meaningless for fans; shown to staff only.
ALTER TABLE users ADD COLUMN email_staff boolean NOT NULL DEFAULT true;

-- One email per notification (the notification's user is the recipient). provider_message_id is the sender's id for
-- the message, when it gives one; error is the sender's reason for a failed send (never the email itself).
CREATE TABLE email_deliveries (
  notification_id     uuid PRIMARY KEY REFERENCES notifications ON DELETE CASCADE,
  sent_at             timestamptz NOT NULL DEFAULT now(),
  status              text NOT NULL CONSTRAINT email_deliveries_status_check CHECK (status IN ('sent', 'failed')),
  provider_message_id text,
  error               text
);
CREATE INDEX email_deliveries_sent_idx ON email_deliveries (sent_at);

-- A new staff notice for a staff member who wants email queues it (the job checks the settings again when it runs,
-- and whether email is configured at all).
CREATE FUNCTION trg_notifications_queue_email() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.kind LIKE 'staff\_%'
     AND EXISTS (SELECT 1 FROM users WHERE id = NEW.user_id AND is_staff AND email_staff AND email IS NOT NULL AND deleted_at IS NULL) THEN
    PERFORM enqueue_job('notifications.email', jsonb_build_object('notification_id', NEW.id), 'email:' || NEW.id);
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER notifications_queue_email AFTER INSERT ON notifications
  FOR EACH ROW EXECUTE FUNCTION trg_notifications_queue_email();

-- Delivery records follow the push ones out after 30 days (privacy.retention, 0053). Same signature, one more row.
CREATE OR REPLACE FUNCTION prune_expired_records(p_sessions interval DEFAULT interval '30 days',
                                                 p_idempotency interval DEFAULT interval '24 hours',
                                                 p_push interval DEFAULT interval '30 days')
RETURNS TABLE (record text, removed integer) LANGUAGE plpgsql AS $$
DECLARE v integer;
BEGIN
  DELETE FROM auth_sessions
   WHERE expires_at < now() - p_sessions OR revoked_at < now() - p_sessions;   -- refresh tokens cascade
  GET DIAGNOSTICS v = ROW_COUNT; record := 'auth_sessions'; removed := v; RETURN NEXT;

  DELETE FROM idempotency_keys WHERE created_at < now() - p_idempotency;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'idempotency_keys'; removed := v; RETURN NEXT;

  DELETE FROM push_deliveries WHERE sent_at < now() - p_push;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'push_deliveries'; removed := v; RETURN NEXT;

  DELETE FROM push_devices WHERE disabled_at < now() - p_push;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'push_devices'; removed := v; RETURN NEXT;

  DELETE FROM email_deliveries WHERE sent_at < now() - p_push;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'email_deliveries'; removed := v; RETURN NEXT;
END $$;
