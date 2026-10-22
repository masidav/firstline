-- Allocations (Phase 11, first part): staff enter shows and set aside fan allocations for them. An allocation is
-- a pool of face-value tickets for an event, open to fans at or above a percentile threshold for the artist,
-- with a registration window. Fan registration and selection come next.
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
