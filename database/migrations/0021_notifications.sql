-- In-app notifications (Phase 9, 2026-10-25): tell fans and staff when something happened that is theirs to act
-- on. Push and email are later channels over the same rows (push needs device tokens, email a provider: both open).
-- Fans: the draw picked you (draw_selected) or not (draw_not_selected); a returned seat came to you (seat_backfilled);
--       your ticket has been delivered (ticket_delivered, written by the worker's notifications.tickets_delivered
--       job when a reservation's deliver_at passes, since that is a time and not a change).
-- Staff (every live staff account): a post got its first open report (staff_report), proof waits for review
--       (staff_review), a draw ran (staff_draw).
-- One row per person, kind, and subject (the registration, reservation, report, submission, or allocation), so
-- retries and repeated triggers never notify twice. Rows are written in the same transaction as the change.

CREATE TABLE notifications (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id    uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  kind       text NOT NULL CONSTRAINT notifications_kind_check CHECK (kind IN (
               'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered',
               'staff_report', 'staff_review', 'staff_draw')),
  subject_id uuid NOT NULL,
  data       jsonb NOT NULL DEFAULT '{}',
  created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  read_at    timestamptz,
  CONSTRAINT notifications_once UNIQUE (user_id, kind, subject_id)
);
CREATE INDEX notifications_user_idx ON notifications (user_id, created_at DESC);
CREATE INDEX notifications_unread_idx ON notifications (user_id) WHERE read_at IS NULL;

-- Only read_at changes after a row is written, and only from null to a time.
CREATE FUNCTION trg_notifications_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.id, NEW.user_id, NEW.kind, NEW.subject_id, NEW.data, NEW.created_at)
     IS DISTINCT FROM (OLD.id, OLD.user_id, OLD.kind, OLD.subject_id, OLD.data, OLD.created_at)
     OR (OLD.read_at IS NOT NULL AND NEW.read_at IS DISTINCT FROM OLD.read_at) THEN
    RAISE EXCEPTION 'notification: only read_at changes, once' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_notifications_guard BEFORE UPDATE ON notifications
  FOR EACH ROW EXECUTE FUNCTION trg_notifications_guard();

-- A live account gets the notification once; a deleted one gets nothing.
CREATE FUNCTION notify(p_user uuid, p_kind text, p_subject uuid, p_data jsonb DEFAULT '{}') RETURNS void LANGUAGE sql AS $$
  INSERT INTO notifications (user_id, kind, subject_id, data)
  SELECT p_user, p_kind, p_subject, p_data FROM users WHERE id = p_user AND deleted_at IS NULL
  ON CONFLICT ON CONSTRAINT notifications_once DO NOTHING;
$$;

CREATE FUNCTION notify_staff(p_kind text, p_subject uuid, p_data jsonb DEFAULT '{}') RETURNS void LANGUAGE sql AS $$
  INSERT INTO notifications (user_id, kind, subject_id, data)
  SELECT id, p_kind, p_subject, p_data FROM users WHERE is_staff AND deleted_at IS NULL
  ON CONFLICT ON CONSTRAINT notifications_once DO NOTHING;
$$;

-- The show and seats, for the text a fan sees.
CREATE FUNCTION registration_notice_data(p_registration uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object('allocation_id', r.allocation_id, 'event_id', r.event_id, 'event_name', e.name,
                            'starts_at', e.starts_at, 'seats', r.seats, 'artist_name', a.name)
    FROM allocation_registrations r JOIN events e ON e.id = r.event_id JOIN artists a ON a.id = e.artist_id
   WHERE r.id = p_registration;
$$;

-- ---------------------------------------------------------------- fans: the draw and backfill
CREATE FUNCTION trg_registration_notify() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.withdrawn_at IS NOT NULL THEN
    RETURN NULL;
  END IF;
  IF OLD.outcome IS NULL AND NEW.outcome = 'selected' THEN
    PERFORM notify(NEW.user_id, 'draw_selected', NEW.id, registration_notice_data(NEW.id));
  ELSIF OLD.outcome IS NULL AND NEW.outcome = 'not_selected' THEN
    PERFORM notify(NEW.user_id, 'draw_not_selected', NEW.id, registration_notice_data(NEW.id));
  ELSIF OLD.outcome = 'not_selected' AND NEW.outcome = 'selected' THEN
    PERFORM notify(NEW.user_id, 'seat_backfilled', NEW.id, registration_notice_data(NEW.id));
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_registration_notify AFTER UPDATE OF outcome ON allocation_registrations
  FOR EACH ROW WHEN (NEW.outcome IS DISTINCT FROM OLD.outcome)
  EXECUTE FUNCTION trg_registration_notify();

-- Tickets whose delivery time has passed and whose holder hasn't been told: called by the worker. Shows that
-- already started are skipped (nothing to act on). Returns how many were written.
CREATE FUNCTION notify_delivered_tickets() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE v_count integer;
BEGIN
  INSERT INTO notifications (user_id, kind, subject_id, data)
  SELECT res.user_id, 'ticket_delivered', res.id,
         jsonb_build_object('reservation_id', res.id, 'event_id', res.event_id, 'event_name', e.name, 'starts_at', e.starts_at,
                            'seats', res.seats, 'artist_name', a.name)
    FROM reservations res JOIN events e ON e.id = res.event_id JOIN artists a ON a.id = e.artist_id
    JOIN users u ON u.id = res.user_id AND u.deleted_at IS NULL
   WHERE res.returned_at IS NULL AND res.cancelled_at IS NULL AND res.deliver_at <= clock_timestamp() AND e.starts_at > clock_timestamp()
  ON CONFLICT ON CONSTRAINT notifications_once DO NOTHING;
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END $$;

-- ---------------------------------------------------------------- staff
CREATE FUNCTION trg_selection_notify_staff() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM notify_staff('staff_draw', NEW.allocation_id,
    (SELECT jsonb_build_object('allocation_id', NEW.allocation_id, 'event_id', al.event_id, 'event_name', e.name,
                               'selected_fans', NEW.selected_fans, 'registrations', NEW.registrations,
                               'seats_assigned', NEW.seats_assigned, 'tickets', NEW.tickets)
       FROM allocations al JOIN events e ON e.id = al.event_id WHERE al.id = NEW.allocation_id));
  RETURN NULL;
END $$;
CREATE TRIGGER trg_selection_notify_staff AFTER INSERT ON allocation_selections
  FOR EACH ROW EXECUTE FUNCTION trg_selection_notify_staff();

-- The first open report on a post (not every report: the queue groups them).
CREATE FUNCTION trg_report_notify_staff() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM reports r WHERE r.target_type = NEW.target_type AND r.target_id = NEW.target_id
                   AND r.resolved_at IS NULL AND r.id <> NEW.id) THEN
    PERFORM notify_staff('staff_report', NEW.id,
      jsonb_build_object('report_id', NEW.id, 'target_type', NEW.target_type, 'target_id', NEW.target_id, 'reason', NEW.reason));
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_report_notify_staff AFTER INSERT ON reports
  FOR EACH ROW EXECUTE FUNCTION trg_report_notify_staff();

CREATE FUNCTION trg_submission_notify_staff() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM notify_staff('staff_review', NEW.id,
    (SELECT jsonb_build_object('submission_id', NEW.id, 'kind', NEW.kind::text, 'artist_name', a.name, 'handle', u.handle::text)
       FROM artists a, users u WHERE a.id = NEW.artist_id AND u.id = NEW.user_id));
  RETURN NULL;
END $$;
CREATE TRIGGER trg_submission_notify_staff_insert AFTER INSERT ON submissions
  FOR EACH ROW WHEN (NEW.status = 'needs_review')
  EXECUTE FUNCTION trg_submission_notify_staff();
CREATE TRIGGER trg_submission_notify_staff_update AFTER UPDATE OF status ON submissions
  FOR EACH ROW WHEN (NEW.status = 'needs_review' AND OLD.status IS DISTINCT FROM 'needs_review')
  EXECUTE FUNCTION trg_submission_notify_staff();

-- ---------------------------------------------------------------- account deletion
-- Notifications are personal: a deleted account's go at once.
CREATE FUNCTION trg_user_notifications_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM notifications WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_notifications_scrub AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_notifications_scrub();
