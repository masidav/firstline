-- Reservations (Phase 11, 2026-10-25): what a selected fan holds. The draw (0017) gives a fan one or two seats at
-- face value; this migration turns that into a reservation with a non-transferable ticket token:
-- - Non-transferable: the reservation belongs to the fan's account (and names the +1's account, if any); the token
--   is only ever shown to its holder through the API, never moved to another account.
-- - Delayed delivery: the token is withheld until `delivery_hours_before_show` hours before the show (24 by
--   default; part of the allocation's rules, so it locks once registration opens). Until then the fan sees that
--   they hold a seat and when the ticket arrives.
-- - Face-value exchange: a fan who can't attend returns the reservation before the show starts. The seats go,
--   at the same face value, to the next fan down the draw's ranking who wasn't selected (backfill), so nobody
--   resells and no seat stays empty. Money doesn't move here yet: checkout through a primary ticketer is a
--   separate item (PROJECT_TODO.md, Phase 11).
-- Provisional decisions recorded in PROJECT_TODO.md: 24 hours before the show; returned seats go to the next in
-- the ranking; a returned +1 reservation frees both seats.

-- ---------------------------------------------------------------- delivery rule on the allocation
ALTER TABLE allocations
  ADD COLUMN delivery_hours_before_show integer NOT NULL DEFAULT 24
    CONSTRAINT allocations_delivery_hours_check CHECK (delivery_hours_before_show >= 0);

-- Same as 0017, with the delivery rule among the locked columns.
CREATE OR REPLACE FUNCTION trg_allocation_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE e record; publishing boolean;
BEGIN
  IF TG_OP = 'UPDATE' THEN
    IF NEW.event_id IS DISTINCT FROM OLD.event_id THEN
      RAISE EXCEPTION 'allocation: an allocation belongs to its show for good' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.published_at IS NOT NULL AND NEW.published_at IS DISTINCT FROM OLD.published_at THEN
      RAISE EXCEPTION 'allocation: publishing cannot be undone' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.cancelled_at IS NOT NULL THEN
      RAISE EXCEPTION 'allocation: a cancelled allocation can''t change' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.selected_at IS DISTINCT FROM OLD.selected_at
       AND (OLD.selected_at IS NOT NULL OR current_setting('fandom.selecting', true) IS DISTINCT FROM NEW.id::text) THEN
      RAISE EXCEPTION 'allocation: only the selection draw sets selected_at, once' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.selected_at IS NOT NULL AND NEW.cancelled_at IS NOT NULL THEN
      RAISE EXCEPTION 'allocation: fans have been selected, so it can''t be cancelled here' USING ERRCODE = 'P0001';
    END IF;
    IF OLD.published_at IS NOT NULL AND clock_timestamp() >= OLD.registration_opens_at
       AND (NEW.name, NEW.tickets, NEW.face_value_cents, NEW.currency, NEW.min_raw_score,
            NEW.registration_opens_at, NEW.registration_closes_at, NEW.delivery_hours_before_show)
           IS DISTINCT FROM
           (OLD.name, OLD.tickets, OLD.face_value_cents, OLD.currency, OLD.min_raw_score,
            OLD.registration_opens_at, OLD.registration_closes_at, OLD.delivery_hours_before_show) THEN
      RAISE EXCEPTION 'allocation: rules are locked once registration opens' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := clock_timestamp();
  ELSIF NEW.selected_at IS NOT NULL THEN
    RAISE EXCEPTION 'allocation: only the selection draw sets selected_at, once' USING ERRCODE = 'P0001';
  END IF;

  publishing := NEW.published_at IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.published_at IS NULL);
  IF publishing THEN
    NEW.published_at := clock_timestamp();
  END IF;
  IF NEW.cancelled_at IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.cancelled_at IS NULL) THEN
    NEW.cancelled_at := clock_timestamp();
  END IF;

  SELECT ev.starts_at, ev.city_id, ev.artist_id, a.status INTO e
    FROM events ev JOIN artists a ON a.id = ev.artist_id
   WHERE ev.id = NEW.event_id
     FOR SHARE OF ev, a;
  IF NEW.registration_closes_at >= e.starts_at THEN
    RAISE EXCEPTION 'allocation: registration must close before the show starts' USING ERRCODE = 'P0001';
  END IF;
  IF publishing THEN
    IF e.city_id IS NULL THEN
      RAISE EXCEPTION 'allocation: the show needs a city before publishing' USING ERRCODE = 'P0001';
    END IF;
    IF e.status <> 'active' THEN
      RAISE EXCEPTION 'allocation: the artist must be active to publish' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.registration_closes_at <= clock_timestamp() THEN
      RAISE EXCEPTION 'allocation: registration would already be over' USING ERRCODE = 'P0001';
    END IF;
    NEW.min_raw_score := COALESCE(NEW.min_raw_score, allocation_default_threshold(e.artist_id));
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------- reservations
CREATE TABLE reservations (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  registration_id  uuid NOT NULL UNIQUE REFERENCES allocation_registrations ON DELETE RESTRICT,
  allocation_id    uuid NOT NULL REFERENCES allocations ON DELETE RESTRICT,
  event_id         uuid NOT NULL REFERENCES events ON DELETE RESTRICT,
  user_id          uuid NOT NULL REFERENCES users ON DELETE RESTRICT,     -- the holder; never changes
  seats            smallint NOT NULL CHECK (seats IN (1, 2)),
  face_value_cents integer NOT NULL CHECK (face_value_cents >= 0),
  currency         text NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  token            text NOT NULL UNIQUE CHECK (token ~ '^[0-9a-f]{64}$'), -- shown to the holder once delivered
  deliver_at       timestamptz NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT clock_timestamp(),
  returned_at      timestamptz,                                           -- given back by the fan
  cancelled_at     timestamptz,
  cancelled_reason text CHECK (cancelled_reason IN ('account_deleted', 'show_cancelled')),
  CONSTRAINT reservations_one_ending CHECK (returned_at IS NULL OR cancelled_at IS NULL),
  CONSTRAINT reservations_cancel_reason CHECK ((cancelled_at IS NULL) = (cancelled_reason IS NULL))
);
CREATE INDEX reservations_user_idx ON reservations (user_id, created_at DESC);
CREATE INDEX reservations_allocation_live_idx ON reservations (allocation_id) WHERE returned_at IS NULL AND cancelled_at IS NULL;

-- Rows are made by the registration trigger below (fandom.reserving = the registration id), seats follow the
-- registration, a return or cancellation is stamped once by its function (fandom.returning / fandom.cancelling),
-- and nothing else changes: holder, show, price, and token are fixed. Never deleted.
CREATE FUNCTION trg_reservation_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
    RAISE EXCEPTION 'reservation: reservations are returned or cancelled, never deleted' USING ERRCODE = 'P0001';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF current_setting('fandom.reserving', true) IS DISTINCT FROM NEW.registration_id::text THEN
      RAISE EXCEPTION 'reservation: only a selected registration makes a reservation' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.returned_at IS NOT NULL OR NEW.cancelled_at IS NOT NULL THEN
      RAISE EXCEPTION 'reservation: a new reservation is live' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;

  IF (NEW.id, NEW.registration_id, NEW.allocation_id, NEW.event_id, NEW.user_id, NEW.face_value_cents, NEW.currency,
      NEW.token, NEW.deliver_at, NEW.created_at)
     IS DISTINCT FROM
     (OLD.id, OLD.registration_id, OLD.allocation_id, OLD.event_id, OLD.user_id, OLD.face_value_cents, OLD.currency,
      OLD.token, OLD.deliver_at, OLD.created_at) THEN
    RAISE EXCEPTION 'reservation: the holder, show, price, token, and delivery time are fixed' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.seats IS DISTINCT FROM OLD.seats
     AND NOT (current_setting('fandom.reserving', true) IS NOT DISTINCT FROM NEW.registration_id::text
              AND OLD.returned_at IS NULL AND OLD.cancelled_at IS NULL) THEN
    RAISE EXCEPTION 'reservation: seats follow the registration' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.returned_at IS DISTINCT FROM OLD.returned_at
     AND NOT (OLD.returned_at IS NULL AND OLD.cancelled_at IS NULL
              AND current_setting('fandom.returning', true) IS NOT DISTINCT FROM NEW.id::text) THEN
    RAISE EXCEPTION 'reservation: only return_reservation() returns a reservation, once' USING ERRCODE = 'P0001';
  END IF;
  IF (NEW.cancelled_at, NEW.cancelled_reason) IS DISTINCT FROM (OLD.cancelled_at, OLD.cancelled_reason)
     AND NOT (OLD.cancelled_at IS NULL AND OLD.returned_at IS NULL
              AND current_setting('fandom.cancelling', true) IS NOT DISTINCT FROM NEW.user_id::text) THEN
    RAISE EXCEPTION 'reservation: only cancel_user_reservations() cancels a reservation, once' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_reservation_guard BEFORE INSERT OR UPDATE OR DELETE ON reservations
  FOR EACH ROW EXECUTE FUNCTION trg_reservation_guard();
CREATE TRIGGER trg_reservations_no_truncate BEFORE TRUNCATE ON reservations
  FOR EACH STATEMENT EXECUTE FUNCTION trg_reservation_guard();

-- An unguessable token: two random UUIDs and the clock, hashed. Core Postgres only (no pgcrypto).
CREATE FUNCTION new_reservation_token() RETURNS text LANGUAGE sql VOLATILE AS $$
  SELECT encode(sha256(convert_to(gen_random_uuid()::text || gen_random_uuid()::text || clock_timestamp()::text, 'UTF8')), 'hex');
$$;

-- A registration that becomes selected (the draw, or a backfill after a return) gets its reservation; when its
-- seats change (add_plus_one) the live reservation follows.
CREATE FUNCTION trg_registration_reserve() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM set_config('fandom.reserving', NEW.id::text, true);
  IF NEW.outcome = 'selected' AND OLD.outcome IS DISTINCT FROM 'selected' THEN
    INSERT INTO reservations (registration_id, allocation_id, event_id, user_id, seats, face_value_cents, currency, token, deliver_at)
    SELECT NEW.id, NEW.allocation_id, NEW.event_id, NEW.user_id, NEW.seats, a.face_value_cents, a.currency, new_reservation_token(),
           ev.starts_at - make_interval(hours => a.delivery_hours_before_show)
      FROM allocations a JOIN events ev ON ev.id = a.event_id
     WHERE a.id = NEW.allocation_id;
  ELSIF NEW.seats IS DISTINCT FROM OLD.seats THEN
    UPDATE reservations SET seats = NEW.seats
     WHERE registration_id = NEW.id AND returned_at IS NULL AND cancelled_at IS NULL;
  END IF;
  PERFORM set_config('fandom.reserving', '', true);
  RETURN NULL;
END $$;
CREATE TRIGGER trg_registration_reserve AFTER UPDATE OF outcome, seats ON allocation_registrations
  FOR EACH ROW EXECUTE FUNCTION trg_registration_reserve();

-- ---------------------------------------------------------------- guards that learn about backfill
-- Same as 0017, plus: backfill_allocation() (fandom.backfilling = the allocation id) may turn a not-selected
-- registration into a selected one, keeping its rank.
CREATE OR REPLACE FUNCTION trg_registration_selection_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE drawing boolean := current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text;
        backfilling boolean := current_setting('fandom.backfilling', true) IS NOT DISTINCT FROM NEW.allocation_id::text;
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
       AND NOT (NEW.guest_user_id IS NULL AND NEW.withdrawn_reason IS NOT DISTINCT FROM 'account_deleted') THEN
      RAISE EXCEPTION 'registration: a guest is named once' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.guest_user_id IS NOT NULL THEN
      NEW.guest_named_at := clock_timestamp();
    END IF;
  END IF;
  RETURN NEW;
END $$;

-- Same as 0017, plus: seats_assigned moves (within the tickets) when a reservation is returned or cancelled and
-- when the freed seats are handed on (fandom.seats_change = the allocation id). The draw's own numbers
-- (registrations, selected_fans, seed) stay what they were.
CREATE OR REPLACE FUNCTION trg_allocation_selections_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text THEN
    RETURN NEW;
  END IF;
  IF TG_OP = 'UPDATE'
     AND (NEW.allocation_id, NEW.seed, NEW.tickets, NEW.registrations, NEW.selected_fans, NEW.ran_by, NEW.ran_at)
         IS NOT DISTINCT FROM (OLD.allocation_id, OLD.seed, OLD.tickets, OLD.registrations, OLD.selected_fans, OLD.ran_by, OLD.ran_at)
     AND ((current_setting('fandom.adding_plus_one', true) IS NOT NULL AND NEW.seats_assigned = OLD.seats_assigned + 1)
          OR current_setting('fandom.seats_change', true) IS NOT DISTINCT FROM NEW.allocation_id::text) THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'selection: draws are recorded once by run_allocation_selection() and never edited' USING ERRCODE = 'P0001';
END $$;

-- ---------------------------------------------------------------- backfill, return, cancel
-- Hands free seats to the next fans down the draw's ranking who weren't selected, until the seats run out or the
-- ranking does. Same +1 rule as the draw: with one seat left, a fan who asked for a +1 gets it alone. Called with
-- the allocation locked FOR UPDATE. Returns how many fans were added.
CREATE FUNCTION backfill_allocation(p_allocation uuid) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE s allocation_selections; r record; v_free integer; v_seats smallint; v_added integer := 0;
BEGIN
  SELECT * INTO s FROM allocation_selections WHERE allocation_id = p_allocation FOR UPDATE;
  IF NOT FOUND THEN RETURN 0; END IF;   -- no draw yet: nothing to hand on
  v_free := s.tickets - s.seats_assigned;
  PERFORM set_config('fandom.backfilling', p_allocation::text, true);
  FOR r IN SELECT reg.id, reg.plus_one FROM allocation_registrations reg
            WHERE reg.allocation_id = p_allocation AND reg.withdrawn_at IS NULL AND reg.outcome = 'not_selected'
            ORDER BY reg.selection_rank LOOP
    EXIT WHEN v_free <= 0;
    v_seats := CASE WHEN r.plus_one AND v_free >= 2 THEN 2 ELSE 1 END;
    UPDATE allocation_registrations SET outcome = 'selected', seats = v_seats WHERE id = r.id;
    v_free := v_free - v_seats;
    v_added := v_added + 1;
  END LOOP;
  PERFORM set_config('fandom.backfilling', '', true);
  PERFORM set_config('fandom.seats_change', p_allocation::text, true);
  UPDATE allocation_selections SET seats_assigned = s.tickets - v_free WHERE allocation_id = p_allocation;
  PERFORM set_config('fandom.seats_change', '', true);
  RETURN v_added;
END $$;

-- The fan gives their reservation back (face-value exchange): allowed until the show starts; idempotent. The seats
-- go to the next fans in the ranking in the same transaction.
CREATE FUNCTION return_reservation(p_user uuid, p_reservation uuid) RETURNS reservations LANGUAGE plpgsql AS $$
DECLARE rec record; res reservations;
BEGIN
  SELECT r.*, ev.starts_at INTO rec
    FROM reservations r JOIN events ev ON ev.id = r.event_id
   WHERE r.id = p_reservation
     FOR UPDATE OF r;
  IF NOT FOUND OR rec.user_id <> p_user THEN RAISE EXCEPTION 'return blocked: not_found' USING ERRCODE = 'P0001'; END IF;
  IF rec.returned_at IS NOT NULL THEN
    SELECT * INTO res FROM reservations WHERE id = p_reservation;
    RETURN res;
  END IF;
  IF rec.cancelled_at IS NOT NULL THEN RAISE EXCEPTION 'return blocked: cancelled' USING ERRCODE = 'P0001'; END IF;
  IF clock_timestamp() >= rec.starts_at THEN RAISE EXCEPTION 'return blocked: show_started' USING ERRCODE = 'P0001'; END IF;

  PERFORM 1 FROM allocations WHERE id = rec.allocation_id FOR UPDATE;   -- serialises with add_plus_one and other returns
  PERFORM set_config('fandom.returning', p_reservation::text, true);
  UPDATE reservations SET returned_at = clock_timestamp() WHERE id = p_reservation RETURNING * INTO res;
  PERFORM set_config('fandom.returning', '', true);
  PERFORM set_config('fandom.seats_change', rec.allocation_id::text, true);
  UPDATE allocation_selections SET seats_assigned = seats_assigned - rec.seats WHERE allocation_id = rec.allocation_id;
  PERFORM set_config('fandom.seats_change', '', true);
  PERFORM backfill_allocation(rec.allocation_id);
  RETURN res;
END $$;

-- Account deletion cancels the account's live reservations and hands the seats on. (0013/0017 withdraw the
-- registrations and drop the fan's own guest in their own trigger.)
CREATE FUNCTION cancel_user_reservations(p_user uuid) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT res.id, res.allocation_id, res.seats FROM reservations res
            WHERE res.user_id = p_user AND res.returned_at IS NULL AND res.cancelled_at IS NULL
            ORDER BY res.created_at LOOP
    PERFORM 1 FROM allocations WHERE id = r.allocation_id FOR UPDATE;
    PERFORM set_config('fandom.cancelling', p_user::text, true);
    UPDATE reservations SET cancelled_at = clock_timestamp(), cancelled_reason = 'account_deleted' WHERE id = r.id;
    PERFORM set_config('fandom.cancelling', '', true);
    PERFORM set_config('fandom.seats_change', r.allocation_id::text, true);
    UPDATE allocation_selections SET seats_assigned = seats_assigned - r.seats WHERE allocation_id = r.allocation_id;
    PERFORM set_config('fandom.seats_change', '', true);
    PERFORM backfill_allocation(r.allocation_id);
    v_count := v_count + 1;
  END LOOP;
  RETURN v_count;
END $$;

CREATE FUNCTION trg_user_reservations_cancel() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM cancel_user_reservations(NEW.id);
  RETURN NULL;
END $$;
-- Runs before trg_user_registrations_withdraw (triggers fire in name order), so the seats are handed on while the
-- deleted fan's registration still reads as live for the counts above.
CREATE TRIGGER trg_user_a_reservations_cancel AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_reservations_cancel();

-- Same as 0017, plus: a fan who returned their reservation can't add a +1 to it.
CREATE OR REPLACE FUNCTION add_plus_one(p_user uuid, p_allocation uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r record; v_free integer;
BEGIN
  PERFORM 1 FROM allocations WHERE id = p_allocation FOR UPDATE;
  SELECT reg.*, ev.starts_at INTO r
    FROM allocation_registrations reg JOIN events ev ON ev.id = reg.event_id
   WHERE reg.allocation_id = p_allocation AND reg.user_id = p_user AND reg.withdrawn_at IS NULL
     FOR UPDATE OF reg;
  IF NOT FOUND THEN RAISE EXCEPTION 'plus one blocked: not_registered' USING ERRCODE = 'P0001'; END IF;
  IF r.outcome IS DISTINCT FROM 'selected' THEN RAISE EXCEPTION 'plus one blocked: not_selected' USING ERRCODE = 'P0001'; END IF;
  IF EXISTS (SELECT 1 FROM reservations res WHERE res.registration_id = r.id AND (res.returned_at IS NOT NULL OR res.cancelled_at IS NOT NULL)) THEN
    RAISE EXCEPTION 'plus one blocked: returned' USING ERRCODE = 'P0001';
  END IF;
  IF r.seats = 2 THEN RETURN; END IF;
  IF clock_timestamp() >= r.starts_at THEN RAISE EXCEPTION 'plus one blocked: show_started' USING ERRCODE = 'P0001'; END IF;
  SELECT tickets - seats_assigned INTO v_free FROM allocation_selections WHERE allocation_id = p_allocation FOR UPDATE;
  IF v_free < 1 THEN RAISE EXCEPTION 'plus one blocked: sold_out' USING ERRCODE = 'P0001'; END IF;

  PERFORM set_config('fandom.adding_plus_one', r.id::text, true);
  UPDATE allocation_registrations SET plus_one = true, seats = 2 WHERE id = r.id;
  UPDATE allocation_selections SET seats_assigned = seats_assigned + 1 WHERE allocation_id = p_allocation;
  PERFORM set_config('fandom.adding_plus_one', '', true);
END $$;

-- Fans selected before this migration get their reservations now (the trigger only sees later changes).
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT reg.* FROM allocation_registrations reg
            WHERE reg.outcome = 'selected' AND reg.withdrawn_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM reservations res WHERE res.registration_id = reg.id) LOOP
    PERFORM set_config('fandom.reserving', r.id::text, true);
    INSERT INTO reservations (registration_id, allocation_id, event_id, user_id, seats, face_value_cents, currency, token, deliver_at)
    SELECT r.id, r.allocation_id, r.event_id, r.user_id, r.seats, a.face_value_cents, a.currency, new_reservation_token(),
           ev.starts_at - make_interval(hours => a.delivery_hours_before_show)
      FROM allocations a JOIN events ev ON ev.id = a.event_id
     WHERE a.id = r.allocation_id;
  END LOOP;
  PERFORM set_config('fandom.reserving', '', true);
END $$;
