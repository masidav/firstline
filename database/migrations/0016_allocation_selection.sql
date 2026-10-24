-- Allocations, part 2 (decided 2026-10-24): a raw-score threshold, +1 guests, and the selection draw.
-- - Threshold: a minimum raw score for the artist, replacing min_percentile. When an allocation is published
--   without one, it gets the raw score a fan with only two saved albums would have (allocation_default_threshold),
--   and like every rule it locks once registration opens.
-- - +1: a fan can ask for a +1 when registering (and change their mind while registration is open), or add one
--   after being selected while seats remain. A +1 takes a seat from the allocation. The guest needs an account,
--   which can be created later: the fan names the guest by account once it exists.
-- - Selection (after registration closes): fans whose hub cities include the show's city first, then everyone
--   else; each group ranked by the raw score in their registration snapshot; equal scores ordered by a stored
--   random seed, so the draw can be checked and repeated. Provisional (PROJECT_TODO.md): when only one seat is
--   left for a fan who asked for a +1, they get the seat without the +1.
-- See services/api/src/features/allocations/README.md.

-- ---------------------------------------------------------------- threshold
-- The raw score of a fan whose only signal is two saved albums by the artist, under the scoring rules in force
-- (album_saves_score from 0015). Real fans with two saved albums usually score a little more (tenure, other
-- signals), so this is a low bar, as intended.
CREATE FUNCTION allocation_default_threshold(p_artist uuid) RETURNS numeric LANGUAGE plpgsql STABLE AS $$
BEGIN
  RETURN round(album_saves_score(p_artist, 2), 2);
END $$;

ALTER TABLE allocations
  ADD COLUMN min_raw_score numeric(10,2) CONSTRAINT allocations_min_raw_score_check CHECK (min_raw_score >= 0),
  ADD COLUMN selected_at   timestamptz;     -- when the draw ran; set only by run_allocation_selection()

-- Rules lock once registration opens (now including min_raw_score); selected_at only by the draw.
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
    -- Once registration has opened, only cancelling is allowed (fans registered under these rules).
    IF OLD.published_at IS NOT NULL AND clock_timestamp() >= OLD.registration_opens_at
       AND (NEW.name, NEW.tickets, NEW.face_value_cents, NEW.currency, NEW.min_raw_score,
            NEW.registration_opens_at, NEW.registration_closes_at)
           IS DISTINCT FROM
           (OLD.name, OLD.tickets, OLD.face_value_cents, OLD.currency, OLD.min_raw_score,
            OLD.registration_opens_at, OLD.registration_closes_at) THEN
      RAISE EXCEPTION 'allocation: rules are locked once registration opens' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := clock_timestamp();
  ELSIF NEW.selected_at IS NOT NULL THEN
    RAISE EXCEPTION 'allocation: only the selection draw sets selected_at, once' USING ERRCODE = 'P0001';
  END IF;

  -- Publishing and cancelling are stamped by the database, never back- or forward-dated.
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
    -- No threshold set: the two-saved-albums default under today's scoring rules, fixed from now on.
    NEW.min_raw_score := COALESCE(NEW.min_raw_score, allocation_default_threshold(e.artist_id));
  END IF;
  RETURN NEW;
END $$;

-- ---------------------------------------------------------------- +1 and selection results
ALTER TABLE allocation_registrations
  ADD COLUMN plus_one      boolean NOT NULL DEFAULT false,
  -- The +1's account, named by the fan once it exists. RESTRICT: users are soft-deleted, never removed.
  ADD COLUMN guest_user_id uuid REFERENCES users ON DELETE RESTRICT,
  ADD COLUMN guest_named_at timestamptz,
  -- Set by the draw: selected or not, place in the ranking (1 = first), and seats (1, or 2 with the +1).
  ADD COLUMN outcome       text CONSTRAINT allocation_registrations_outcome_check
                                CHECK (outcome IN ('selected', 'not_selected')),
  ADD COLUMN selection_rank integer CHECK (selection_rank > 0),
  ADD COLUMN seats         smallint CHECK (seats IN (1, 2)),
  ADD CONSTRAINT allocation_registrations_seats_outcome CHECK ((outcome = 'selected') = (seats IS NOT NULL)),
  ADD CONSTRAINT allocation_registrations_guest_check
    CHECK ((guest_user_id IS NULL) = (guest_named_at IS NULL) AND (guest_user_id IS NULL OR plus_one)),
  ADD CONSTRAINT allocation_registrations_guest_not_fan CHECK (guest_user_id <> user_id);
CREATE INDEX allocation_registrations_guest_idx ON allocation_registrations (event_id, guest_user_id)
  WHERE guest_user_id IS NOT NULL;

-- The 0013 guard keeps the snapshot fixed. This one guards the columns above: outcome, rank, and seats only from
-- the draw (fandom.selecting = the allocation id); plus_one changes only while registration is open, or from
-- false to true after being selected (add_plus_one checks a seat is free); the guest is named once.
CREATE FUNCTION trg_registration_selection_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE drawing boolean := current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text;
        v_closes timestamptz;
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.outcome IS NOT NULL OR NEW.selection_rank IS NOT NULL OR NEW.seats IS NOT NULL OR NEW.guest_user_id IS NOT NULL THEN
      RAISE EXCEPTION 'registration: a new registration has no outcome or guest yet' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;

  IF (NEW.outcome, NEW.selection_rank) IS DISTINCT FROM (OLD.outcome, OLD.selection_rank)
     AND (NOT drawing OR OLD.outcome IS NOT NULL) THEN
    RAISE EXCEPTION 'registration: only the selection draw sets the outcome, once' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.seats IS DISTINCT FROM OLD.seats
     AND NOT drawing AND NOT (OLD.seats = 1 AND NEW.seats = 2 AND NEW.plus_one AND current_setting('fandom.adding_plus_one', true) = NEW.id::text) THEN
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
CREATE TRIGGER trg_registration_selection_guard BEFORE INSERT OR UPDATE ON allocation_registrations
  FOR EACH ROW EXECUTE FUNCTION trg_registration_selection_guard();

-- Account deletion (0013 withdraws registrations and empties hubs) also drops the fan's own guest. A deleted guest
-- stays named on the registration for the audit; the ticket flow will refuse a deleted account.
CREATE OR REPLACE FUNCTION trg_user_registrations_withdraw() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  UPDATE allocation_registrations
     SET withdrawn_at = COALESCE(withdrawn_at, clock_timestamp()), withdrawn_reason = 'account_deleted',
         hub_city_ids = '{}', hub_match = NULL, guest_user_id = NULL, guest_named_at = NULL
   WHERE user_id = NEW.id;
  RETURN NULL;
END $$;

-- ---------------------------------------------------------------- the draw
-- One row per allocation, written once by run_allocation_selection(). With the registrations' snapshots this is
-- everything needed to repeat the draw: same seed, same order.
CREATE TABLE allocation_selections (
  allocation_id   uuid PRIMARY KEY REFERENCES allocations ON DELETE RESTRICT,
  seed            text NOT NULL CHECK (seed <> ''),
  tickets         integer NOT NULL CHECK (tickets > 0),
  registrations   integer NOT NULL CHECK (registrations >= 0),     -- live registrations in the draw
  selected_fans   integer NOT NULL CHECK (selected_fans >= 0),
  seats_assigned  integer NOT NULL CHECK (seats_assigned BETWEEN 0 AND tickets),
  ran_by          uuid REFERENCES users ON DELETE SET NULL,
  ran_at          timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE FUNCTION trg_allocation_selections_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text THEN
    RETURN NEW;
  END IF;
  -- seats_assigned grows when a selected fan adds a +1 afterwards; nothing else changes.
  IF TG_OP = 'UPDATE' AND current_setting('fandom.adding_plus_one', true) IS NOT NULL
     AND (NEW.allocation_id, NEW.seed, NEW.tickets, NEW.registrations, NEW.selected_fans, NEW.ran_by, NEW.ran_at)
         IS NOT DISTINCT FROM (OLD.allocation_id, OLD.seed, OLD.tickets, OLD.registrations, OLD.selected_fans, OLD.ran_by, OLD.ran_at)
     AND NEW.seats_assigned = OLD.seats_assigned + 1 THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'selection: draws are recorded once by run_allocation_selection() and never edited' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_allocation_selections_guard BEFORE INSERT OR UPDATE OR DELETE ON allocation_selections
  FOR EACH ROW EXECUTE FUNCTION trg_allocation_selections_guard();
CREATE TRIGGER trg_allocation_selections_no_truncate BEFORE TRUNCATE ON allocation_selections
  FOR EACH STATEMENT EXECUTE FUNCTION trg_allocation_selections_guard();

-- The ranking, as a query anyone can rerun with the stored seed: hub fans first, then by snapshot raw score, then
-- by md5(seed || registration id). Selection walks it in order.
CREATE FUNCTION allocation_ranking(p_allocation uuid, p_seed text)
RETURNS TABLE (registration_id uuid, rank integer, plus_one boolean) LANGUAGE sql STABLE AS $$
  SELECT r.id, (row_number() OVER w)::integer, r.plus_one
    FROM allocation_registrations r
   WHERE r.allocation_id = p_allocation AND r.withdrawn_at IS NULL
  WINDOW w AS (ORDER BY (r.hub_match IS NULL), r.raw_score DESC, md5(p_seed || r.id::text))
   ORDER BY 2;
$$;

-- Runs the draw once registration has closed. Idempotent: a second call returns the recorded draw unchanged.
-- Locks the allocation FOR UPDATE, so registrations still in flight (which hold it FOR SHARE) finish first.
CREATE FUNCTION run_allocation_selection(p_allocation uuid, p_by uuid) RETURNS allocation_selections
LANGUAGE plpgsql AS $$
DECLARE al record; s allocation_selections; v_seed text := gen_random_uuid()::text;
        v_left integer; v_selected integer := 0; v_count integer := 0; r record; v_seats smallint;
BEGIN
  SELECT a.*, ar.status AS artist_status INTO al
    FROM allocations a JOIN events ev ON ev.id = a.event_id JOIN artists ar ON ar.id = ev.artist_id
   WHERE a.id = p_allocation
     FOR UPDATE OF a;
  IF NOT FOUND OR al.published_at IS NULL THEN
    RAISE EXCEPTION 'selection blocked: not_found' USING ERRCODE = 'P0001';
  END IF;
  SELECT * INTO s FROM allocation_selections WHERE allocation_id = p_allocation;
  IF FOUND THEN
    RETURN s;
  END IF;
  IF al.cancelled_at IS NOT NULL THEN RAISE EXCEPTION 'selection blocked: cancelled' USING ERRCODE = 'P0001'; END IF;
  IF clock_timestamp() < al.registration_closes_at THEN
    RAISE EXCEPTION 'selection blocked: registration_open' USING ERRCODE = 'P0001';
  END IF;
  IF al.artist_status <> 'active' THEN RAISE EXCEPTION 'selection blocked: artist_not_active' USING ERRCODE = 'P0001'; END IF;

  PERFORM set_config('fandom.selecting', p_allocation::text, true);
  v_left := al.tickets;
  FOR r IN SELECT * FROM allocation_ranking(p_allocation, v_seed) LOOP
    v_count := v_count + 1;
    v_seats := CASE WHEN v_left <= 0 THEN NULL WHEN r.plus_one AND v_left >= 2 THEN 2 ELSE 1 END;
    UPDATE allocation_registrations
       SET outcome = CASE WHEN v_seats IS NULL THEN 'not_selected' ELSE 'selected' END,
           selection_rank = r.rank, seats = v_seats
     WHERE id = r.registration_id;
    IF v_seats IS NOT NULL THEN
      v_left := v_left - v_seats;
      v_selected := v_selected + 1;
    END IF;
  END LOOP;

  INSERT INTO allocation_selections (allocation_id, seed, tickets, registrations, selected_fans, seats_assigned, ran_by)
  VALUES (p_allocation, v_seed, al.tickets, v_count, v_selected, al.tickets - v_left, p_by)
  RETURNING * INTO s;
  UPDATE allocations SET selected_at = s.ran_at WHERE id = p_allocation;
  PERFORM set_config('fandom.selecting', '', true);
  RETURN s;
END $$;

-- A selected fan adds a +1 after the draw, if a seat is still free and the show hasn't started. Idempotent.
CREATE FUNCTION add_plus_one(p_user uuid, p_allocation uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r record; v_free integer;
BEGIN
  PERFORM 1 FROM allocations WHERE id = p_allocation FOR UPDATE;
  SELECT reg.*, ev.starts_at INTO r
    FROM allocation_registrations reg JOIN events ev ON ev.id = reg.event_id
   WHERE reg.allocation_id = p_allocation AND reg.user_id = p_user AND reg.withdrawn_at IS NULL
     FOR UPDATE OF reg;
  IF NOT FOUND THEN RAISE EXCEPTION 'plus one blocked: not_registered' USING ERRCODE = 'P0001'; END IF;
  IF r.outcome IS DISTINCT FROM 'selected' THEN RAISE EXCEPTION 'plus one blocked: not_selected' USING ERRCODE = 'P0001'; END IF;
  IF r.seats = 2 THEN RETURN; END IF;
  IF clock_timestamp() >= r.starts_at THEN RAISE EXCEPTION 'plus one blocked: show_started' USING ERRCODE = 'P0001'; END IF;
  SELECT tickets - seats_assigned INTO v_free FROM allocation_selections WHERE allocation_id = p_allocation FOR UPDATE;
  IF v_free < 1 THEN RAISE EXCEPTION 'plus one blocked: sold_out' USING ERRCODE = 'P0001'; END IF;

  PERFORM set_config('fandom.adding_plus_one', r.id::text, true);
  UPDATE allocation_registrations SET plus_one = true, seats = 2 WHERE id = r.id;
  UPDATE allocation_selections SET seats_assigned = seats_assigned + 1 WHERE allocation_id = p_allocation;
  PERFORM set_config('fandom.adding_plus_one', '', true);
END $$;

-- Names the +1's account. Before the draw the fan only needs to have asked for a +1; after it, the +1 must have a
-- seat. The guest can't be the fan, a deleted account, registered for the same show, or another fan's guest for it.
CREATE FUNCTION name_plus_one_guest(p_user uuid, p_allocation uuid, p_guest uuid) RETURNS void LANGUAGE plpgsql AS $$
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
  -- Lock the guest's account so two fans can't name the same guest at once.
  PERFORM 1 FROM users WHERE id = p_guest AND deleted_at IS NULL FOR NO KEY UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'guest blocked: guest_not_found' USING ERRCODE = 'P0001'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations x WHERE x.event_id = r.event_id AND x.withdrawn_at IS NULL
               AND (x.user_id = p_guest OR x.guest_user_id = p_guest)) THEN
    RAISE EXCEPTION 'guest blocked: guest_has_ticket' USING ERRCODE = 'P0001';
  END IF;
  UPDATE allocation_registrations SET guest_user_id = p_guest WHERE id = r.id;
END $$;

-- ---------------------------------------------------------------- registration threshold
-- Same checks as 0013, with the raw-score threshold in place of the percentile.
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
  -- Someone's named guest for this show already has a seat coming; they can't also register for it.
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.guest_user_id = p_user
               AND r.withdrawn_at IS NULL) THEN
    RETURN 'already_registered';
  END IF;
  RETURN NULL;
END $$;

ALTER TABLE allocations DROP COLUMN min_percentile;
