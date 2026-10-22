-- Hub cities (decided 2026-10-20/21): each fan picks a primary city and up to two backups from a curated list of
-- US and Canadian metro areas. Allocations put fans whose hubs include the show's city first. Required before the
-- app can be used (the app asks right after sign-up). Changes are allowed once a week; the first choice doesn't
-- count as a change.

CREATE TABLE cities (
  id       text PRIMARY KEY CHECK (id ~ '^[a-z]{2}-[a-z]{2}-[a-z0-9-]+$'),   -- country-region-name, e.g. us-il-chicago
  name     text NOT NULL CHECK (name <> ''),
  region   text NOT NULL CHECK (region ~ '^[A-Z]{2}$'),                     -- state or province code
  country  text NOT NULL CHECK (country ~ '^[A-Z]{2}$'),                    -- ISO 3166-1 alpha-2
  active   boolean NOT NULL DEFAULT true,                                   -- retire a city instead of deleting it
  UNIQUE (country, region, name)
);
CREATE INDEX cities_name_idx ON cities (lower(name));

-- Position 1 is the primary city. Positions are 1..n with no gaps (set_hub_cities writes them).
CREATE TABLE user_hub_cities (
  user_id  uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  position smallint NOT NULL CHECK (position BETWEEN 1 AND 3),
  city_id  text NOT NULL REFERENCES cities,
  PRIMARY KEY (user_id, position),
  UNIQUE (user_id, city_id)
);
CREATE INDEX user_hub_cities_city_idx ON user_hub_cities (city_id);

-- Last real change (not the first choice); the next change is allowed a week later.
ALTER TABLE users ADD COLUMN hubs_changed_at timestamptz;

-- Replaces a fan's hub cities with p_cities (primary first). Enforces 1 to 3 distinct active cities and the weekly
-- limit. The same list again is not a change. Returns when the next change is allowed (null: any time).
CREATE FUNCTION set_hub_cities(p_user uuid, p_cities text[], p_min_interval interval DEFAULT interval '7 days')
RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE v_current text[]; v_changed timestamptz; v_n integer := COALESCE(array_length(p_cities, 1), 0);
BEGIN
  IF v_n NOT BETWEEN 1 AND 3 THEN
    RAISE EXCEPTION 'hub cities: choose 1 to 3 cities' USING ERRCODE = 'P0001';
  END IF;
  IF (SELECT count(DISTINCT c) FROM unnest(p_cities) c) <> v_n THEN
    RAISE EXCEPTION 'hub cities: a city is listed twice' USING ERRCODE = 'P0001';
  END IF;
  IF (SELECT count(*) FROM cities WHERE id = ANY(p_cities) AND active) <> v_n THEN
    RAISE EXCEPTION 'hub cities: unknown city' USING ERRCODE = 'P0001';
  END IF;

  SELECT hubs_changed_at INTO v_changed FROM users WHERE id = p_user AND deleted_at IS NULL FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'user % does not exist', p_user;
  END IF;
  SELECT array_agg(city_id ORDER BY position) INTO v_current FROM user_hub_cities WHERE user_id = p_user;

  IF v_current IS NOT DISTINCT FROM p_cities THEN
    RETURN CASE WHEN v_changed + p_min_interval > now() THEN v_changed + p_min_interval END;
  END IF;
  IF v_current IS NOT NULL AND v_changed + p_min_interval > now() THEN
    RAISE EXCEPTION 'hub change limit: next change at %', v_changed + p_min_interval USING ERRCODE = 'P0001';
  END IF;

  DELETE FROM user_hub_cities WHERE user_id = p_user;
  INSERT INTO user_hub_cities (user_id, position, city_id)
  SELECT p_user, ord, city FROM unnest(p_cities) WITH ORDINALITY AS t (city, ord);
  IF v_current IS NOT NULL THEN
    UPDATE users SET hubs_changed_at = now() WHERE id = p_user;
    RETURN now() + p_min_interval;
  END IF;
  RETURN NULL;
END $$;

-- Hub cities are location data: account deletion removes them with the other personal data.
CREATE FUNCTION trg_user_hubs_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM user_hub_cities WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_hubs_scrub AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL) EXECUTE FUNCTION trg_user_hubs_scrub();

-- Starter list: major US and Canadian metro areas, by principal city. Add rows to cover more; set active = false
-- to retire one (fans keep it until they change).
INSERT INTO cities (id, name, region, country) VALUES
  ('us-ny-new-york', 'New York', 'NY', 'US'),
  ('us-ca-los-angeles', 'Los Angeles', 'CA', 'US'),
  ('us-il-chicago', 'Chicago', 'IL', 'US'),
  ('us-tx-dallas', 'Dallas', 'TX', 'US'),
  ('us-tx-houston', 'Houston', 'TX', 'US'),
  ('us-dc-washington', 'Washington', 'DC', 'US'),
  ('us-pa-philadelphia', 'Philadelphia', 'PA', 'US'),
  ('us-ga-atlanta', 'Atlanta', 'GA', 'US'),
  ('us-fl-miami', 'Miami', 'FL', 'US'),
  ('us-az-phoenix', 'Phoenix', 'AZ', 'US'),
  ('us-ma-boston', 'Boston', 'MA', 'US'),
  ('us-ca-riverside', 'Riverside', 'CA', 'US'),
  ('us-ca-san-francisco', 'San Francisco', 'CA', 'US'),
  ('us-ca-oakland', 'Oakland', 'CA', 'US'),
  ('us-mi-detroit', 'Detroit', 'MI', 'US'),
  ('us-wa-seattle', 'Seattle', 'WA', 'US'),
  ('us-mn-minneapolis', 'Minneapolis', 'MN', 'US'),
  ('us-ca-san-diego', 'San Diego', 'CA', 'US'),
  ('us-fl-tampa', 'Tampa', 'FL', 'US'),
  ('us-co-denver', 'Denver', 'CO', 'US'),
  ('us-md-baltimore', 'Baltimore', 'MD', 'US'),
  ('us-mo-st-louis', 'St. Louis', 'MO', 'US'),
  ('us-fl-orlando', 'Orlando', 'FL', 'US'),
  ('us-nc-charlotte', 'Charlotte', 'NC', 'US'),
  ('us-tx-san-antonio', 'San Antonio', 'TX', 'US'),
  ('us-or-portland', 'Portland', 'OR', 'US'),
  ('us-ca-sacramento', 'Sacramento', 'CA', 'US'),
  ('us-pa-pittsburgh', 'Pittsburgh', 'PA', 'US'),
  ('us-tx-austin', 'Austin', 'TX', 'US'),
  ('us-nv-las-vegas', 'Las Vegas', 'NV', 'US'),
  ('us-oh-cincinnati', 'Cincinnati', 'OH', 'US'),
  ('us-mo-kansas-city', 'Kansas City', 'MO', 'US'),
  ('us-oh-columbus', 'Columbus', 'OH', 'US'),
  ('us-in-indianapolis', 'Indianapolis', 'IN', 'US'),
  ('us-oh-cleveland', 'Cleveland', 'OH', 'US'),
  ('us-ca-san-jose', 'San Jose', 'CA', 'US'),
  ('us-tn-nashville', 'Nashville', 'TN', 'US'),
  ('us-va-virginia-beach', 'Virginia Beach', 'VA', 'US'),
  ('us-ri-providence', 'Providence', 'RI', 'US'),
  ('us-fl-jacksonville', 'Jacksonville', 'FL', 'US'),
  ('us-wi-milwaukee', 'Milwaukee', 'WI', 'US'),
  ('us-nc-raleigh', 'Raleigh', 'NC', 'US'),
  ('us-nc-durham', 'Durham', 'NC', 'US'),
  ('us-ok-oklahoma-city', 'Oklahoma City', 'OK', 'US'),
  ('us-tn-memphis', 'Memphis', 'TN', 'US'),
  ('us-va-richmond', 'Richmond', 'VA', 'US'),
  ('us-ky-louisville', 'Louisville', 'KY', 'US'),
  ('us-la-new-orleans', 'New Orleans', 'LA', 'US'),
  ('us-ut-salt-lake-city', 'Salt Lake City', 'UT', 'US'),
  ('us-ct-hartford', 'Hartford', 'CT', 'US'),
  ('us-ct-new-haven', 'New Haven', 'CT', 'US'),
  ('us-ny-buffalo', 'Buffalo', 'NY', 'US'),
  ('us-al-birmingham', 'Birmingham', 'AL', 'US'),
  ('us-ny-rochester', 'Rochester', 'NY', 'US'),
  ('us-mi-grand-rapids', 'Grand Rapids', 'MI', 'US'),
  ('us-az-tucson', 'Tucson', 'AZ', 'US'),
  ('us-hi-honolulu', 'Honolulu', 'HI', 'US'),
  ('us-ok-tulsa', 'Tulsa', 'OK', 'US'),
  ('us-ca-fresno', 'Fresno', 'CA', 'US'),
  ('us-ma-worcester', 'Worcester', 'MA', 'US'),
  ('us-ne-omaha', 'Omaha', 'NE', 'US'),
  ('us-nm-albuquerque', 'Albuquerque', 'NM', 'US'),
  ('us-sc-greenville', 'Greenville', 'SC', 'US'),
  ('us-ny-albany', 'Albany', 'NY', 'US'),
  ('us-ca-bakersfield', 'Bakersfield', 'CA', 'US'),
  ('us-tn-knoxville', 'Knoxville', 'TN', 'US'),
  ('us-tx-el-paso', 'El Paso', 'TX', 'US'),
  ('us-la-baton-rouge', 'Baton Rouge', 'LA', 'US'),
  ('us-sc-columbia', 'Columbia', 'SC', 'US'),
  ('us-sc-charleston', 'Charleston', 'SC', 'US'),
  ('us-id-boise', 'Boise', 'ID', 'US'),
  ('us-wi-madison', 'Madison', 'WI', 'US'),
  ('us-ia-des-moines', 'Des Moines', 'IA', 'US'),
  ('us-ar-little-rock', 'Little Rock', 'AR', 'US'),
  ('us-wa-spokane', 'Spokane', 'WA', 'US'),
  ('us-ny-syracuse', 'Syracuse', 'NY', 'US'),
  ('us-nc-asheville', 'Asheville', 'NC', 'US'),
  ('us-vt-burlington', 'Burlington', 'VT', 'US'),
  ('us-ak-anchorage', 'Anchorage', 'AK', 'US'),
  ('us-me-portland', 'Portland', 'ME', 'US'),
  ('us-ga-savannah', 'Savannah', 'GA', 'US'),
  ('us-tn-chattanooga', 'Chattanooga', 'TN', 'US'),
  ('us-ky-lexington', 'Lexington', 'KY', 'US'),
  ('us-mi-ann-arbor', 'Ann Arbor', 'MI', 'US'),
  ('us-ga-athens', 'Athens', 'GA', 'US'),
  ('us-ks-wichita', 'Wichita', 'KS', 'US'),
  ('us-nv-reno', 'Reno', 'NV', 'US'),
  ('us-ne-lincoln', 'Lincoln', 'NE', 'US'),
  ('us-tx-fort-worth', 'Fort Worth', 'TX', 'US'),
  ('us-mn-st-paul', 'St. Paul', 'MN', 'US'),
  ('ca-on-toronto', 'Toronto', 'ON', 'CA'),
  ('ca-qc-montreal', 'Montreal', 'QC', 'CA'),
  ('ca-bc-vancouver', 'Vancouver', 'BC', 'CA'),
  ('ca-ab-calgary', 'Calgary', 'AB', 'CA'),
  ('ca-ab-edmonton', 'Edmonton', 'AB', 'CA'),
  ('ca-on-ottawa', 'Ottawa', 'ON', 'CA'),
  ('ca-mb-winnipeg', 'Winnipeg', 'MB', 'CA'),
  ('ca-qc-quebec-city', 'Quebec City', 'QC', 'CA'),
  ('ca-on-hamilton', 'Hamilton', 'ON', 'CA'),
  ('ca-on-kitchener', 'Kitchener', 'ON', 'CA'),
  ('ca-on-london', 'London', 'ON', 'CA'),
  ('ca-ns-halifax', 'Halifax', 'NS', 'CA'),
  ('ca-bc-victoria', 'Victoria', 'BC', 'CA'),
  ('ca-sk-saskatoon', 'Saskatoon', 'SK', 'CA'),
  ('ca-sk-regina', 'Regina', 'SK', 'CA'),
  ('ca-nl-st-johns', 'St. John''s', 'NL', 'CA');
