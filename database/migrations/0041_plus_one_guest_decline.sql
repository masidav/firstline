-- Security pass (2026-10-28): naming someone as your +1 used to block them from registering for that show, with no
-- way out: a fan could lock rivals out of a draw by naming them. Now:
-- - the named guest hears about it (notification guest_named),
-- - the guest can decline before the draw (decline_plus_one_invite), which frees them to register themselves,
-- - a fan can't name the same person again for that show once they declined,
-- - registration says why it's blocked (named_as_guest) instead of already_registered.
-- Whether a guest must accept before counting at all is an open product decision (PROJECT_TODO.md).

CREATE TABLE plus_one_declines (
  event_id      uuid NOT NULL REFERENCES events ON DELETE CASCADE,
  guest_user_id uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  host_user_id  uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  declined_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
  PRIMARY KEY (event_id, guest_user_id, host_user_id)
);

-- Same as 0020, plus: decline_plus_one_invite() (fandom.declining_guest = the registration id) may clear the guest.
CREATE OR REPLACE FUNCTION trg_registration_selection_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE drawing boolean := current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text;
        backfilling boolean := current_setting('fandom.backfilling', true) IS NOT DISTINCT FROM NEW.allocation_id::text;
        declining boolean := current_setting('fandom.declining_guest', true) IS NOT DISTINCT FROM NEW.id::text;
        v_closes timestamptz;
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.outcome IS NOT NULL OR NEW.selection_rank IS NOT NULL OR NEW.seats IS NOT NULL OR NEW.guest_user_id IS NOT NULL THEN
      RAISE EXCEPTION 'registration: a new registration has no outcome or guest yet' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;

  IF (NEW.outcome, NEW.selection_rank) IS DISTINCT FROM (OLD.outcome, OLD.selection_rank)
     AND NOT (drawing AND OLD.outcome IS NULL)
     AND NOT (backfilling AND OLD.outcome = 'not_selected' AND NEW.outcome = 'selected'
              AND NEW.selection_rank = OLD.selection_rank AND OLD.withdrawn_at IS NULL) THEN
    RAISE EXCEPTION 'registration: only the selection draw sets the outcome, once' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.seats IS DISTINCT FROM OLD.seats
     AND NOT drawing AND NOT backfilling
     AND NOT (OLD.seats = 1 AND NEW.seats = 2 AND NEW.plus_one AND current_setting('fandom.adding_plus_one', true) = NEW.id::text) THEN
    RAISE EXCEPTION 'registration: seats change only through the draw or add_plus_one()' USING ERRCODE = 'P0001';
  END IF;

  IF NEW.plus_one IS DISTINCT FROM OLD.plus_one AND NOT drawing THEN
    SELECT registration_closes_at INTO v_closes FROM allocations WHERE id = NEW.allocation_id;
    IF NOT (OLD.withdrawn_at IS NULL AND (clock_timestamp() < v_closes
            OR (NEW.plus_one AND OLD.outcome = 'selected' AND NEW.seats = 2))) THEN
      RAISE EXCEPTION 'registration: the +1 can change only while registration is open, or be added once selected' USING ERRCODE = 'P0001';
    END IF;
  END IF;

  IF (NEW.guest_user_id, NEW.guest_named_at) IS DISTINCT FROM (OLD.guest_user_id, OLD.guest_named_at) THEN
    IF OLD.guest_user_id IS NOT NULL
       AND NOT (NEW.guest_user_id IS NULL AND NEW.withdrawn_reason IS NOT DISTINCT FROM 'account_deleted')
       AND NOT (NEW.guest_user_id IS NULL AND declining AND OLD.outcome IS NULL) THEN
      RAISE EXCEPTION 'registration: a guest is named once' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.guest_user_id IS NOT NULL THEN
      NEW.guest_named_at := clock_timestamp();
    END IF;
  END IF;
  RETURN NEW;
END $$;

-- The guest declines being someone's +1 for a show, before the draw. Returns how many invites were declined (0 when
-- there were none, so it's idempotent).
CREATE FUNCTION decline_plus_one_invite(p_guest uuid, p_event uuid) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT id, user_id FROM allocation_registrations
            WHERE event_id = p_event AND guest_user_id = p_guest AND withdrawn_at IS NULL AND outcome IS NULL
              FOR UPDATE LOOP
    PERFORM set_config('fandom.declining_guest', r.id::text, true);
    UPDATE allocation_registrations SET guest_user_id = NULL, guest_named_at = NULL WHERE id = r.id;
    PERFORM set_config('fandom.declining_guest', '', true);
    INSERT INTO plus_one_declines (event_id, guest_user_id, host_user_id) VALUES (p_event, p_guest, r.user_id)
    ON CONFLICT DO NOTHING;
    v_count := v_count + 1;
  END LOOP;
  RETURN v_count;
END $$;

-- Same as 0017, plus: not someone who declined this fan for this show.
CREATE OR REPLACE FUNCTION name_plus_one_guest(p_user uuid, p_allocation uuid, p_guest uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r record;
BEGIN
  SELECT * INTO r FROM allocation_registrations
   WHERE allocation_id = p_allocation AND user_id = p_user AND withdrawn_at IS NULL
     FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'guest blocked: not_registered' USING ERRCODE = 'P0001'; END IF;
  IF NOT r.plus_one OR (r.outcome IS NOT NULL AND r.seats IS DISTINCT FROM 2) THEN
    RAISE EXCEPTION 'guest blocked: no_plus_one' USING ERRCODE = 'P0001';
  END IF;
  IF r.guest_user_id IS NOT DISTINCT FROM p_guest THEN RETURN; END IF;
  IF r.guest_user_id IS NOT NULL THEN RAISE EXCEPTION 'guest blocked: already_named' USING ERRCODE = 'P0001'; END IF;
  IF p_guest = p_user THEN RAISE EXCEPTION 'guest blocked: self' USING ERRCODE = 'P0001'; END IF;
  IF EXISTS (SELECT 1 FROM plus_one_declines d WHERE d.event_id = r.event_id AND d.guest_user_id = p_guest AND d.host_user_id = p_user) THEN
    RAISE EXCEPTION 'guest blocked: declined' USING ERRCODE = 'P0001';
  END IF;
  -- Lock the guest's account so two fans can't name the same guest at once.
  PERFORM 1 FROM users WHERE id = p_guest AND deleted_at IS NULL FOR NO KEY UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'guest blocked: guest_not_found' USING ERRCODE = 'P0001'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations x WHERE x.event_id = r.event_id AND x.withdrawn_at IS NULL
               AND (x.user_id = p_guest OR x.guest_user_id = p_guest)) THEN
    RAISE EXCEPTION 'guest blocked: guest_has_ticket' USING ERRCODE = 'P0001';
  END IF;
  UPDATE allocation_registrations SET guest_user_id = p_guest WHERE id = r.id;
END $$;

-- Same as 0017, with the guest case told apart: named_as_guest instead of already_registered.
CREATE OR REPLACE FUNCTION registration_blocker(p_user uuid, p_allocation uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE al record; v_raw numeric;
BEGIN
  SELECT a.*, ar.status AS artist_status, ev.artist_id INTO al
    FROM allocations a JOIN events ev ON ev.id = a.event_id JOIN artists ar ON ar.id = ev.artist_id
   WHERE a.id = p_allocation;
  IF NOT FOUND OR al.published_at IS NULL OR al.cancelled_at IS NOT NULL
     OR clock_timestamp() < al.registration_opens_at OR clock_timestamp() >= al.registration_closes_at THEN
    RETURN 'not_open';
  END IF;
  IF al.artist_status <> 'active' THEN RETURN 'artist_not_active'; END IF;
  IF NOT EXISTS (SELECT 1 FROM user_hub_cities WHERE user_id = p_user) THEN RETURN 'hub_city_required'; END IF;
  SELECT raw_score INTO v_raw FROM user_artist_scores WHERE user_id = p_user AND artist_id = al.artist_id;
  IF NOT FOUND THEN RETURN 'no_score'; END IF;
  IF v_raw < al.min_raw_score THEN RETURN 'below_threshold'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.user_id = p_user
               AND r.withdrawn_at IS NULL AND r.allocation_id <> p_allocation) THEN
    RETURN 'already_registered';
  END IF;
  -- Someone's named guest for this show already has a seat coming; they can decline the invite to register.
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.guest_user_id = p_user
               AND r.withdrawn_at IS NULL) THEN
    RETURN 'named_as_guest';
  END IF;
  RETURN NULL;
END $$;

-- The guest hears about being named.
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered', 'presale', 'guest_named',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction', 'staff_show_changed'));

CREATE FUNCTION trg_registration_notify_guest() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM notify(NEW.guest_user_id, 'guest_named', NEW.id,
    (SELECT jsonb_build_object('event_id', NEW.event_id, 'event_name', e.name, 'starts_at', e.starts_at,
                               'host_handle', u.handle::text, 'allocation_id', NEW.allocation_id)
       FROM events e, users u WHERE e.id = NEW.event_id AND u.id = NEW.user_id));
  RETURN NULL;
END $$;
CREATE TRIGGER trg_registration_notify_guest AFTER UPDATE OF guest_user_id ON allocation_registrations
  FOR EACH ROW WHEN (NEW.guest_user_id IS NOT NULL AND NEW.guest_user_id IS DISTINCT FROM OLD.guest_user_id)
  EXECUTE FUNCTION trg_registration_notify_guest();
