-- Presale reminders (2026-10-26). A fan hears about a presale for a show they'd care about: one by an artist they
-- have a fandom score for, in one of their hub cities. One reminder per fan per presale, sent once the presale is
-- less than a day away (or at once, if it was announced later than that), never after it ends, and not for a
-- cancelled show. Fans can turn them off. Provisional rules, listed in PROJECT_TODO.md.

ALTER TABLE users ADD COLUMN presale_reminders boolean NOT NULL DEFAULT true;

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered', 'presale',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction'));

-- A presale's stable id: presales are replaced on every sync, so the id comes from what identifies one.
CREATE FUNCTION presale_subject_id(p_event uuid, p_name text, p_starts_at timestamptz) RETURNS uuid
LANGUAGE sql IMMUTABLE AS $$
  SELECT md5(p_event::text || '|' || p_name || '|' || extract(epoch FROM p_starts_at)::text)::uuid;
$$;

-- Writes the reminders that are due; called by the worker (notifications.presales). Returns how many were written.
CREATE FUNCTION notify_presales(p_lead interval DEFAULT interval '24 hours') RETURNS integer LANGUAGE plpgsql AS $$
DECLARE v_count integer;
BEGIN
  INSERT INTO notifications (user_id, kind, subject_id, data)
  SELECT u.id, 'presale', presale_subject_id(p.event_id, p.name, p.starts_at),
         jsonb_build_object('event_id', e.id, 'event_name', e.name, 'artist_name', a.name, 'venue', e.venue,
                            'city', e.city, 'show_starts_at', e.starts_at, 'presale_name', p.name,
                            'starts_at', p.starts_at, 'ends_at', p.ends_at, 'ticket_url', e.ticket_url)
    FROM event_presales p
    JOIN events e ON e.id = p.event_id AND e.status <> 'cancelled' AND e.starts_at > clock_timestamp()
    JOIN artists a ON a.id = e.artist_id AND a.status = 'active'
    JOIN user_hub_cities h ON h.city_id = e.city_id
    JOIN users u ON u.id = h.user_id AND u.deleted_at IS NULL AND u.presale_reminders
    JOIN user_artist_scores sc ON sc.user_id = u.id AND sc.artist_id = e.artist_id
   WHERE p.starts_at <= clock_timestamp() + p_lead
     AND (p.ends_at IS NULL OR p.ends_at > clock_timestamp())
     AND (p.ends_at IS NOT NULL OR p.starts_at > clock_timestamp() - interval '1 day')
  ON CONFLICT ON CONSTRAINT notifications_once DO NOTHING;
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END $$;

CREATE INDEX event_presales_starts_idx ON event_presales (starts_at);
