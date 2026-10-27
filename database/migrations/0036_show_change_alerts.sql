-- Staff hear when Ticketmaster changes a show that has a fan allocation (2026-10-27). The allocation keeps the time and
-- city staff locked (0030), so a new time from the source, or the show being cancelled, postponed, or rescheduled, needs
-- a person: tell the fans, move or cancel the allocation. One notice per change (the subject is derived from the show,
-- its status, and the source's time), to every live staff account.

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered', 'presale',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction', 'staff_show_changed'));

CREATE FUNCTION trg_events_notify_show_changed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_moved boolean; v_status boolean;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM allocations a WHERE a.event_id = NEW.id AND a.cancelled_at IS NULL) THEN
    RETURN NULL;
  END IF;
  v_moved := NEW.source_starts_at IS DISTINCT FROM OLD.source_starts_at AND NEW.source_starts_at IS DISTINCT FROM NEW.starts_at;
  v_status := NEW.status IS DISTINCT FROM OLD.status AND NEW.status IN ('cancelled', 'postponed', 'rescheduled');
  IF v_moved OR v_status THEN
    PERFORM notify_staff('staff_show_changed',
      md5(NEW.id::text || '|' || NEW.status || '|' || COALESCE(extract(epoch FROM NEW.source_starts_at)::text, ''))::uuid,
      (SELECT jsonb_build_object('event_id', NEW.id, 'event_name', NEW.name, 'artist_name', a.name, 'status', NEW.status,
                                 'starts_at', NEW.starts_at, 'source_starts_at', NEW.source_starts_at, 'moved', v_moved)
         FROM artists a WHERE a.id = NEW.artist_id));
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER events_notify_show_changed AFTER UPDATE OF source_starts_at, status ON events
  FOR EACH ROW EXECUTE FUNCTION trg_events_notify_show_changed();
