-- Allocations (Phase 11): staff enter shows and set aside fan allocations for them. An allocation is a pool of
-- face-value tickets for an event, open to fans at or above a percentile threshold for the artist, with a
-- registration window. Fans register while it's open; each registration keeps a snapshot of the fan's hub cities
-- and score. Selection comes next.
-- Provisional (PROJECT_TODO.md): one ticket per fan; the threshold is a percentile; rules lock once registration
-- opens.
-- Time checks use clock_timestamp(), not now(): a transaction that started before registration opened must not
-- be able to change the rules after it has.

-- A show's city is one of the hub cities, so "fans whose hubs include the show's city" is an exact match.
-- created_at is the migration time for shows that existed before it.
ALTER TABLE events
  ADD COLUMN city_id    text REFERENCES cities,
  ADD COLUMN created_by uuid REFERENCES users ON DELETE SET NULL,
  ADD COLUMN created_at timestamptz NOT NULL DEFAULT now();
CREATE INDEX events_city_date_idx ON events (city_id, starts_at);

CREATE TABLE allocations (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  -- RESTRICT: deleting a show (or, through events, an artist) must not silently take its allocations with it.
  event_id               uuid NOT NULL REFERENCES events ON DELETE RESTRICT,
  name                   text NOT NULL DEFAULT 'Fan allocation' CHECK (name <> ''),
  tickets                integer NOT NULL CHECK (tickets > 0),                 -- seats set aside; one per fan
  face_value_cents       integer NOT NULL CHECK (face_value_cents >= 0),
  currency               text NOT NULL DEFAULT 'USD' CHECK (currency ~ '^[A-Z]{3}$'),
  min_percentile         numeric(5,2) NOT NULL DEFAULT 0 CHECK (min_percentile BETWEEN 0 AND 100),
  registration_opens_at  timestamptz NOT NULL,
  registration_closes_at timestamptz NOT NULL,
  published_at           timestamptz,                                          -- null: a draft only staff see
  cancelled_at           timestamptz,
  created_by             uuid REFERENCES users ON DELETE SET NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT allocations_window_check CHECK (registration_closes_at > registration_opens_at),
  CONSTRAINT allocations_cancel_published CHECK (cancelled_at IS NULL OR published_at IS NOT NULL)   -- drafts are deleted
);
CREATE INDEX allocations_event_idx ON allocations (event_id);
CREATE INDEX allocations_open_idx ON allocations (registration_closes_at) WHERE published_at IS NOT NULL AND cancelled_at IS NULL;

-- Checks every insert and update of an allocation against its show and artist, which are locked (FOR SHARE) so a
-- concurrent change to the show or artist waits for this transaction, and vice versa.
CREATE FUNCTION trg_allocation_check() RETURNS trigger LANGUAGE plpgsql AS $$
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
    -- Once registration has opened, only cancelling is allowed (fans registered under these rules).
    IF OLD.published_at IS NOT NULL AND clock_timestamp() >= OLD.registration_opens_at
       AND (NEW.name, NEW.tickets, NEW.face_value_cents, NEW.currency, NEW.min_percentile,
            NEW.registration_opens_at, NEW.registration_closes_at)
           IS DISTINCT FROM
           (OLD.name, OLD.tickets, OLD.face_value_cents, OLD.currency, OLD.min_percentile,
            OLD.registration_opens_at, OLD.registration_closes_at) THEN
      RAISE EXCEPTION 'allocation: rules are locked once registration opens' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := clock_timestamp();
  END IF;

  -- Publishing and cancelling are stamped by the database, never back- or forward-dated.
  publishing := NEW.published_at IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.published_at IS NULL);
  IF publishing THEN
    NEW.published_at := clock_timestamp();
  END IF;
  IF NEW.cancelled_at IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.cancelled_at IS NULL) THEN
    NEW.cancelled_at := clock_timestamp();
  END IF;

  SELECT ev.starts_at, ev.city_id, a.status INTO e
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
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_allocation_check BEFORE INSERT OR UPDATE ON allocations
  FOR EACH ROW EXECUTE FUNCTION trg_allocation_check();

-- Only drafts can be deleted; published allocations are cancelled (and will carry registrations and selections).
CREATE FUNCTION trg_allocation_delete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.published_at IS NOT NULL THEN
    RAISE EXCEPTION 'allocation: a published allocation is cancelled, not deleted' USING ERRCODE = 'P0001';
  END IF;
  RETURN OLD;
END $$;
CREATE TRIGGER trg_allocation_delete BEFORE DELETE ON allocations
  FOR EACH ROW EXECUTE FUNCTION trg_allocation_delete();

-- A show under a published, live allocation keeps its artist and a city, and can't move to before registration
-- closes; once an allocation has opened, its time and city are fixed too (registrations were made against them).
CREATE FUNCTION trg_event_lock() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE live boolean; opened boolean;
BEGIN
  SELECT bool_or(true), bool_or(clock_timestamp() >= a.registration_opens_at) INTO live, opened
    FROM allocations a WHERE a.event_id = OLD.id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL;
  IF EXISTS (SELECT 1 FROM allocations a WHERE a.event_id = OLD.id AND a.cancelled_at IS NULL
               AND a.registration_closes_at >= NEW.starts_at) THEN
    RAISE EXCEPTION 'allocation: registration must close before the show starts' USING ERRCODE = 'P0001';
  END IF;
  IF live AND (NEW.artist_id IS DISTINCT FROM OLD.artist_id OR NEW.city_id IS NULL) THEN
    RAISE EXCEPTION 'event: the artist and city are locked while an allocation is published' USING ERRCODE = 'P0001';
  END IF;
  IF opened AND (NEW.starts_at, NEW.city_id) IS DISTINCT FROM (OLD.starts_at, OLD.city_id) THEN
    RAISE EXCEPTION 'event: time and city are locked once an allocation opens' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_event_lock BEFORE UPDATE ON events
  FOR EACH ROW EXECUTE FUNCTION trg_event_lock();

-- ---------------------------------------------------------------- registrations
-- One live registration per fan per show (a show can have several allocations). Withdrawing keeps the row, so
-- every registration that existed when selection ran can be audited. The snapshot is what the fan had when they
-- registered: hub cities (decided 2026-10-20: a later hub change doesn't affect it) and score.
-- Concurrency: register and withdraw lock the allocation, show, and artist FOR SHARE and the fan's score row, so
-- the checks and the stored snapshot agree; selection will lock the allocation FOR UPDATE after registration
-- closes, so it waits for any registration still in flight.

-- event_id on a registration must be its allocation's show (allocations never change show).
ALTER TABLE allocations ADD CONSTRAINT allocations_id_event_key UNIQUE (id, event_id);

CREATE TABLE allocation_registrations (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  allocation_id     uuid NOT NULL,
  event_id          uuid NOT NULL,
  user_id           uuid NOT NULL REFERENCES users ON DELETE RESTRICT,      -- users are soft-deleted, never removed
  hub_city_ids      text[] NOT NULL,                       -- primary first; emptied when the account is deleted
  hub_match         smallint CHECK (hub_match BETWEEN 1 AND 3),   -- position of the show's city in the hubs; null: none
  percentile        numeric(5,2) NOT NULL CHECK (percentile BETWEEN 0 AND 100),
  raw_score         numeric(10,2) NOT NULL,
  is_estimated      boolean NOT NULL,
  baseline_weight   numeric(4,3) NOT NULL,
  score_computed_at timestamptz NOT NULL,                  -- which scoring run the snapshot came from
  registered_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
  withdrawn_at      timestamptz,
  withdrawn_reason  text CHECK (withdrawn_reason IN ('fan', 'allocation_cancelled', 'account_deleted')),
  FOREIGN KEY (allocation_id, event_id) REFERENCES allocations (id, event_id) ON DELETE RESTRICT,
  CHECK ((withdrawn_at IS NULL) = (withdrawn_reason IS NULL)),
  CHECK (withdrawn_at IS NULL OR withdrawn_at >= registered_at),
  CHECK (cardinality(hub_city_ids) BETWEEN 1 AND 3 OR withdrawn_reason = 'account_deleted')
);
CREATE UNIQUE INDEX allocation_registrations_live_uidx
  ON allocation_registrations (event_id, user_id) WHERE withdrawn_at IS NULL;
CREATE INDEX allocation_registrations_allocation_idx ON allocation_registrations (allocation_id) WHERE withdrawn_at IS NULL;
CREATE INDEX allocation_registrations_user_idx ON allocation_registrations (user_id, registered_at DESC);

-- Registrations are created and withdrawn through the functions below. The database stamps the times; a fan's
-- registration and withdrawal must happen before registration closes. The snapshot never changes, except that
-- account deletion empties the hub cities (location data). Registrations are never deleted.
CREATE FUNCTION trg_registration_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_closes timestamptz; v_cancelled timestamptz;
BEGIN
  IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
    RAISE EXCEPTION 'registration: registrations are withdrawn, not deleted' USING ERRCODE = 'P0001';
  END IF;
  SELECT registration_closes_at, cancelled_at INTO v_closes, v_cancelled FROM allocations WHERE id = NEW.allocation_id;

  IF TG_OP = 'INSERT' THEN
    NEW.registered_at := clock_timestamp();
    IF NEW.withdrawn_at IS NOT NULL OR NEW.registered_at >= v_closes THEN
      RAISE EXCEPTION 'registration blocked: not_open' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;

  -- UPDATE: only withdrawing a live registration, or scrubbing a deleted account's hub cities.
  IF (NEW.id, NEW.allocation_id, NEW.event_id, NEW.user_id, NEW.percentile, NEW.raw_score, NEW.is_estimated,
      NEW.baseline_weight, NEW.score_computed_at, NEW.registered_at)
     IS DISTINCT FROM
     (OLD.id, OLD.allocation_id, OLD.event_id, OLD.user_id, OLD.percentile, OLD.raw_score, OLD.is_estimated,
      OLD.baseline_weight, OLD.score_computed_at, OLD.registered_at) THEN
    RAISE EXCEPTION 'registration: only withdrawing a live registration is allowed' USING ERRCODE = 'P0001';
  END IF;
  -- NULL-safe throughout: a NULL reason must never let a change through.
  IF (NEW.hub_city_ids, NEW.hub_match) IS DISTINCT FROM (OLD.hub_city_ids, OLD.hub_match)
     AND NOT (NEW.withdrawn_reason IS NOT DISTINCT FROM 'account_deleted' AND NEW.hub_city_ids = '{}' AND NEW.hub_match IS NULL) THEN
    RAISE EXCEPTION 'registration: only withdrawing a live registration is allowed' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.withdrawn_at IS NOT NULL THEN
    IF (NEW.withdrawn_at, NEW.withdrawn_reason) IS DISTINCT FROM (OLD.withdrawn_at, OLD.withdrawn_reason)
       AND NOT (NEW.withdrawn_reason IS NOT DISTINCT FROM 'account_deleted' AND NEW.withdrawn_at IS NOT DISTINCT FROM OLD.withdrawn_at) THEN
      RAISE EXCEPTION 'registration: only withdrawing a live registration is allowed' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;
  IF NEW.withdrawn_at IS NOT NULL THEN
    NEW.withdrawn_at := clock_timestamp();
    IF NEW.withdrawn_reason IS NOT DISTINCT FROM 'fan' AND v_cancelled IS NULL AND NEW.withdrawn_at >= v_closes THEN
      RAISE EXCEPTION 'registration blocked: not_open' USING ERRCODE = 'P0001';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_registration_guard BEFORE INSERT OR UPDATE OR DELETE ON allocation_registrations
  FOR EACH ROW EXECUTE FUNCTION trg_registration_guard();
CREATE TRIGGER trg_registration_no_truncate BEFORE TRUNCATE ON allocation_registrations
  FOR EACH STATEMENT EXECUTE FUNCTION trg_registration_guard();

-- Why a fan can't register for an allocation right now, or null if they can. Reasons, in the order checked:
-- not_open, artist_not_active, hub_city_required, no_score, below_threshold, already_registered (for another
-- allocation of the same show). Registering again for the same allocation is not a reason: it's a no-op.
CREATE FUNCTION registration_blocker(p_user uuid, p_allocation uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE al record; v_percentile numeric;
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
  SELECT percentile INTO v_percentile FROM user_artist_scores WHERE user_id = p_user AND artist_id = al.artist_id;
  IF NOT FOUND THEN RETURN 'no_score'; END IF;
  IF v_percentile < al.min_percentile THEN RETURN 'below_threshold'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.user_id = p_user
               AND r.withdrawn_at IS NULL AND r.allocation_id <> p_allocation) THEN
    RETURN 'already_registered';
  END IF;
  RETURN NULL;
END $$;

-- Registers a fan, with the snapshot. Idempotent: an existing live registration for this allocation is returned.
-- Everything the checks read is locked first, so the checks and the stored snapshot see the same data.
CREATE FUNCTION register_for_allocation(p_user uuid, p_allocation uuid) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_id uuid; v_blocker text; v_event uuid; v_city text; v_artist uuid; v_hubs text[];
BEGIN
  SELECT a.event_id, ev.city_id, ev.artist_id INTO v_event, v_city, v_artist
    FROM allocations a JOIN events ev ON ev.id = a.event_id JOIN artists ar ON ar.id = ev.artist_id
   WHERE a.id = p_allocation
     FOR SHARE OF a, ev, ar;
  -- One registration at a time per fan (also serializes with hub city changes).
  PERFORM 1 FROM users WHERE id = p_user AND deleted_at IS NULL FOR NO KEY UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'registration blocked: account_deleted' USING ERRCODE = 'P0001';
  END IF;
  PERFORM 1 FROM user_artist_scores WHERE user_id = p_user AND artist_id = v_artist FOR SHARE;

  SELECT id INTO v_id FROM allocation_registrations
   WHERE allocation_id = p_allocation AND user_id = p_user AND withdrawn_at IS NULL;
  IF FOUND THEN
    RETURN v_id;
  END IF;
  v_blocker := registration_blocker(p_user, p_allocation);
  IF v_blocker IS NOT NULL THEN
    RAISE EXCEPTION 'registration blocked: %', v_blocker USING ERRCODE = 'P0001';
  END IF;

  SELECT array_agg(city_id ORDER BY position) INTO v_hubs FROM user_hub_cities WHERE user_id = p_user;
  INSERT INTO allocation_registrations (allocation_id, event_id, user_id, hub_city_ids, hub_match, percentile,
                                        raw_score, is_estimated, baseline_weight, score_computed_at)
  SELECT p_allocation, v_event, p_user, v_hubs, array_position(v_hubs, v_city), s.percentile, s.raw_score,
         s.is_estimated, s.baseline_weight, s.computed_at
    FROM user_artist_scores s
   WHERE s.user_id = p_user AND s.artist_id = v_artist
  RETURNING id INTO v_id;
  IF v_id IS NULL THEN
    RAISE EXCEPTION 'registration blocked: no_score' USING ERRCODE = 'P0001';
  END IF;
  RETURN v_id;
END $$;

-- Withdraws a fan's live registration while registration is open. Returns false if there was none.
CREATE FUNCTION withdraw_registration(p_user uuid, p_allocation uuid) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM allocations WHERE id = p_allocation FOR SHARE;
  UPDATE allocation_registrations SET withdrawn_at = clock_timestamp(), withdrawn_reason = 'fan'
   WHERE allocation_id = p_allocation AND user_id = p_user AND withdrawn_at IS NULL;
  RETURN FOUND;
END $$;

-- Cancelling an allocation withdraws its registrations, so those fans can register for a replacement.
CREATE FUNCTION trg_allocation_cancel_registrations() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  UPDATE allocation_registrations SET withdrawn_at = clock_timestamp(), withdrawn_reason = 'allocation_cancelled'
   WHERE allocation_id = NEW.id AND withdrawn_at IS NULL;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_allocation_cancel_registrations AFTER UPDATE OF cancelled_at ON allocations
  FOR EACH ROW WHEN (OLD.cancelled_at IS NULL AND NEW.cancelled_at IS NOT NULL)
  EXECUTE FUNCTION trg_allocation_cancel_registrations();

-- Deleting an account withdraws its live registrations and empties the hub cities in all of them (location data,
-- as in 0010). The scores stay for the audit of past selections.
CREATE FUNCTION trg_user_registrations_withdraw() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  UPDATE allocation_registrations
     SET withdrawn_at = COALESCE(withdrawn_at, clock_timestamp()), withdrawn_reason = 'account_deleted',
         hub_city_ids = '{}', hub_match = NULL
   WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_registrations_withdraw AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_registrations_withdraw();
