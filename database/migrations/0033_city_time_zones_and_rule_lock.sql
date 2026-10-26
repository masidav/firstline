-- Allocation hardening (services/api/src/features/allocations/TODO.md, from the 0013 review):
-- 1. Every hub city gets its IANA time zone, so the app can show a show's time and registration window in the
--    show's local time. New cities must name a zone PostgreSQL knows.
-- 2. An allocation's rules also lock once anyone has registered for it, not only once the clock passes
--    registration_opens_at. Registrations can only be made while registration is open, so today the clock rule
--    covers this; the check makes the lock independent of the clock, and of the window being moved.

-- ---------------------------------------------------------------- time zones
ALTER TABLE cities ADD COLUMN time_zone text;

UPDATE cities SET time_zone = CASE region
    -- Canada
    WHEN 'AB' THEN 'America/Edmonton'
    WHEN 'BC' THEN 'America/Vancouver'
    WHEN 'MB' THEN 'America/Winnipeg'
    WHEN 'NL' THEN 'America/St_Johns'
    WHEN 'NS' THEN 'America/Halifax'
    WHEN 'ON' THEN 'America/Toronto'
    WHEN 'QC' THEN 'America/Toronto'
    WHEN 'SK' THEN 'America/Regina'
    -- United States (states in one zone)
    WHEN 'AK' THEN 'America/Anchorage'
    WHEN 'AL' THEN 'America/Chicago'
    WHEN 'AR' THEN 'America/Chicago'
    WHEN 'AZ' THEN 'America/Phoenix'
    WHEN 'CA' THEN 'America/Los_Angeles'
    WHEN 'CO' THEN 'America/Denver'
    WHEN 'CT' THEN 'America/New_York'
    WHEN 'DC' THEN 'America/New_York'
    WHEN 'FL' THEN 'America/New_York'
    WHEN 'GA' THEN 'America/New_York'
    WHEN 'HI' THEN 'Pacific/Honolulu'
    WHEN 'IA' THEN 'America/Chicago'
    WHEN 'ID' THEN 'America/Boise'
    WHEN 'IL' THEN 'America/Chicago'
    WHEN 'IN' THEN 'America/Indiana/Indianapolis'
    WHEN 'KS' THEN 'America/Chicago'
    WHEN 'LA' THEN 'America/Chicago'
    WHEN 'MA' THEN 'America/New_York'
    WHEN 'MD' THEN 'America/New_York'
    WHEN 'ME' THEN 'America/New_York'
    WHEN 'MI' THEN 'America/Detroit'
    WHEN 'MN' THEN 'America/Chicago'
    WHEN 'MO' THEN 'America/Chicago'
    WHEN 'NC' THEN 'America/New_York'
    WHEN 'NE' THEN 'America/Chicago'
    WHEN 'NM' THEN 'America/Denver'
    WHEN 'NV' THEN 'America/Los_Angeles'
    WHEN 'NY' THEN 'America/New_York'
    WHEN 'OH' THEN 'America/New_York'
    WHEN 'OK' THEN 'America/Chicago'
    WHEN 'OR' THEN 'America/Los_Angeles'
    WHEN 'PA' THEN 'America/New_York'
    WHEN 'RI' THEN 'America/New_York'
    WHEN 'SC' THEN 'America/New_York'
    WHEN 'UT' THEN 'America/Denver'
    WHEN 'VA' THEN 'America/New_York'
    WHEN 'VT' THEN 'America/New_York'
    WHEN 'WA' THEN 'America/Los_Angeles'
    WHEN 'WI' THEN 'America/Chicago'
  END;

-- States split across zones: set per city.
UPDATE cities SET time_zone = 'America/New_York'           WHERE id IN ('us-ky-lexington', 'us-tn-chattanooga', 'us-tn-knoxville');
UPDATE cities SET time_zone = 'America/Kentucky/Louisville' WHERE id = 'us-ky-louisville';
UPDATE cities SET time_zone = 'America/Chicago'            WHERE id IN ('us-tn-memphis', 'us-tn-nashville');
UPDATE cities SET time_zone = 'America/Denver'             WHERE id = 'us-tx-el-paso';
UPDATE cities SET time_zone = 'America/Chicago'            WHERE region = 'TX' AND id <> 'us-tx-el-paso';

DO $$
DECLARE missing text;
BEGIN
  SELECT string_agg(id, ', ' ORDER BY id) INTO missing FROM cities
   WHERE time_zone IS NULL OR time_zone NOT IN (SELECT name FROM pg_timezone_names);
  IF missing IS NOT NULL THEN
    RAISE EXCEPTION 'cities without a known time zone: %', missing;
  END IF;
END $$;

ALTER TABLE cities ALTER COLUMN time_zone SET NOT NULL;

-- A CHECK can't read pg_timezone_names (not immutable), so a trigger checks new and changed cities.
CREATE FUNCTION trg_city_time_zone() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  -- NULL is left to the NOT NULL constraint.
  IF NEW.time_zone IS NOT NULL AND NOT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = NEW.time_zone) THEN
    RAISE EXCEPTION 'city: % is not a known time zone', NEW.time_zone USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_city_time_zone BEFORE INSERT OR UPDATE OF time_zone ON cities
  FOR EACH ROW EXECUTE FUNCTION trg_city_time_zone();

-- ---------------------------------------------------------------- rules lock
-- Same as 0020, with "or anyone has registered" added to the lock.
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
    IF (NEW.name, NEW.tickets, NEW.face_value_cents, NEW.currency, NEW.min_raw_score,
        NEW.registration_opens_at, NEW.registration_closes_at, NEW.delivery_hours_before_show)
       IS DISTINCT FROM
       (OLD.name, OLD.tickets, OLD.face_value_cents, OLD.currency, OLD.min_raw_score,
        OLD.registration_opens_at, OLD.registration_closes_at, OLD.delivery_hours_before_show)
       AND OLD.published_at IS NOT NULL
       AND (clock_timestamp() >= OLD.registration_opens_at
            OR EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.allocation_id = OLD.id)) THEN
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
