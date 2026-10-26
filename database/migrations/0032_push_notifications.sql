-- Push notifications (Phase 9, 2026-10-26). The phone app registers an Expo push token per device; every in-app
-- notification is also sent as a push to the fan's live devices, unless they turned that category off. Expo's push
-- service delivers to Apple and Google. Devices Expo reports as gone are switched off.

CREATE TABLE push_devices (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id         uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  token           text NOT NULL CONSTRAINT push_devices_token_key UNIQUE
                    CONSTRAINT push_devices_token_check CHECK (token ~ '^Expo(nent)?PushToken\[[A-Za-z0-9_-]{10,200}\]$'),
  platform        text NOT NULL CHECK (platform IN ('ios', 'android')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  last_seen_at    timestamptz NOT NULL DEFAULT now(),   -- last time the app registered it (each launch)
  disabled_at     timestamptz,                           -- Expo said the device is gone, or the fan signed out
  disabled_reason text
);
CREATE INDEX push_devices_user_idx ON push_devices (user_id) WHERE disabled_at IS NULL;

-- What to push, by category (NotificationText.Category). In-app notifications are written either way.
ALTER TABLE users
  ADD COLUMN push_tickets  boolean NOT NULL DEFAULT true,   -- draw results, seats, delivered tickets
  ADD COLUMN push_presales boolean NOT NULL DEFAULT true,   -- presale reminders
  ADD COLUMN push_staff    boolean NOT NULL DEFAULT true;   -- staff notices (staff only)

-- One push per notification per device. ticket_id is Expo's id for checking the receipt later.
CREATE TABLE push_deliveries (
  notification_id uuid NOT NULL REFERENCES notifications ON DELETE CASCADE,
  device_id       uuid NOT NULL REFERENCES push_devices ON DELETE CASCADE,
  sent_at         timestamptz NOT NULL DEFAULT now(),
  status          text NOT NULL CONSTRAINT push_deliveries_status_check CHECK (status IN ('sent', 'delivered', 'failed')),
  ticket_id       text,
  error           text,
  checked_at      timestamptz,
  PRIMARY KEY (notification_id, device_id)
);
CREATE INDEX push_deliveries_unchecked_idx ON push_deliveries (sent_at) WHERE status = 'sent' AND ticket_id IS NOT NULL;

-- A new notification for a fan with a live device queues its push (the job checks the fan's settings).
CREATE FUNCTION trg_notifications_queue_push() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM push_devices WHERE user_id = NEW.user_id AND disabled_at IS NULL) THEN
    PERFORM enqueue_job('notifications.push', jsonb_build_object('notification_id', NEW.id), 'push:' || NEW.id);
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER notifications_queue_push AFTER INSERT ON notifications
  FOR EACH ROW EXECUTE FUNCTION trg_notifications_queue_push();

-- A deleted account's devices go at once (tokens are personal and would keep pushing).
CREATE FUNCTION trg_user_push_devices_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM push_devices WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_push_devices_scrub AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_push_devices_scrub();
