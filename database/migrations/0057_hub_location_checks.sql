-- Hub location checks (Phase 10, 2026-11-05): a fan's device location, when they choose to share it, confirms they are
-- where their hub cities say they are. Fans in a show's city go first in the draw (0010, 0013), so a hub city is
-- worth lying about, and a farm of accounts all "in Chicago" is cheap to make. Design and privacy trade-off:
-- docs/security/hub-location-checks.md.
--
-- What is stored: never a coordinate. The app sends latitude, longitude, and the accuracy to one API endpoint; they
-- reach one database function as bound parameters (not logged) and leave it as the nearest curated city, the distance
-- to it and to the fan's nearest hub city rounded to 10 km, and whether a hub city is within hub_radius_km. One row per
-- check, kept 90 days, deleted at once with the account, not in the fan's data download (like the 0054 signals;
-- owner decision 2 in the design note). A reading with accuracy worse than 50 km (an IP-level guess) is not recorded.
--
-- Checks happen at three moments, never in the background: when the fan sets their hub cities (hub_set), when they open
-- a show's fan-ticket standing (eligibility), and when they register (registration). Sharing is optional: the app asks,
-- a fan can refuse, and nothing here lowers a score. What it does:
-- - trust part hub_confirmed (fan_trust_parts, 0039): 1 when a check in the last 180 days matched a hub city; the trust
--   score only breaks ties in draws between equal raw scores.
-- - rule hub_location_mismatch (other): min_checks or more checks on distinct days in the last days days, each at least
--   min_km from every hub city the fan had then. The fan is claiming a hub they are never near.
-- - rule impossible_travel (duplicate_account): two checks within max_hours whose nearest cities are min_km or more
--   apart. One account used from two places at once: shared, or a cluster behind one sign-in.
-- Thresholds are data (fraud_rules), provisional (PROJECT_TODO.md).

-- ---------------------------------------------------------------- city coordinates
ALTER TABLE cities
  ADD COLUMN latitude  numeric(8,5) CHECK (latitude BETWEEN -90 AND 90),
  ADD COLUMN longitude numeric(8,5) CHECK (longitude BETWEEN -180 AND 180);

UPDATE cities SET (latitude, longitude) = (x.lat, x.lon)
FROM (VALUES
  ('us-ny-new-york', 40.71280, -74.00600), ('us-ca-los-angeles', 34.05220, -118.24370), ('us-il-chicago', 41.87810, -87.62980),
  ('us-tx-dallas', 32.77670, -96.79700), ('us-tx-houston', 29.76040, -95.36980), ('us-dc-washington', 38.90720, -77.03690),
  ('us-pa-philadelphia', 39.95260, -75.16520), ('us-ga-atlanta', 33.74900, -84.38800), ('us-fl-miami', 25.76170, -80.19180),
  ('us-az-phoenix', 33.44840, -112.07400), ('us-ma-boston', 42.36010, -71.05890), ('us-ca-riverside', 33.98060, -117.37550),
  ('us-ca-san-francisco', 37.77490, -122.41940), ('us-ca-oakland', 37.80440, -122.27120), ('us-mi-detroit', 42.33140, -83.04580),
  ('us-wa-seattle', 47.60620, -122.33210), ('us-mn-minneapolis', 44.97780, -93.26500), ('us-ca-san-diego', 32.71570, -117.16110),
  ('us-fl-tampa', 27.95060, -82.45720), ('us-co-denver', 39.73920, -104.99030), ('us-md-baltimore', 39.29040, -76.61220),
  ('us-mo-st-louis', 38.62700, -90.19940), ('us-fl-orlando', 28.53830, -81.37920), ('us-nc-charlotte', 35.22710, -80.84310),
  ('us-tx-san-antonio', 29.42410, -98.49360), ('us-or-portland', 45.51520, -122.67840), ('us-ca-sacramento', 38.58160, -121.49440),
  ('us-pa-pittsburgh', 40.44060, -79.99590), ('us-tx-austin', 30.26720, -97.74310), ('us-nv-las-vegas', 36.16990, -115.13980),
  ('us-oh-cincinnati', 39.10310, -84.51200), ('us-mo-kansas-city', 39.09970, -94.57860), ('us-oh-columbus', 39.96120, -82.99880),
  ('us-in-indianapolis', 39.76840, -86.15810), ('us-oh-cleveland', 41.49930, -81.69440), ('us-ca-san-jose', 37.33820, -121.88630),
  ('us-tn-nashville', 36.16270, -86.78160), ('us-va-virginia-beach', 36.85290, -75.97800), ('us-ri-providence', 41.82400, -71.41280),
  ('us-fl-jacksonville', 30.33220, -81.65570), ('us-wi-milwaukee', 43.03890, -87.90650), ('us-nc-raleigh', 35.77960, -78.63820),
  ('us-nc-durham', 35.99400, -78.89860), ('us-ok-oklahoma-city', 35.46760, -97.51640), ('us-tn-memphis', 35.14950, -90.04900),
  ('us-va-richmond', 37.54070, -77.43600), ('us-ky-louisville', 38.25270, -85.75850), ('us-la-new-orleans', 29.95110, -90.07150),
  ('us-ut-salt-lake-city', 40.76080, -111.89100), ('us-ct-hartford', 41.76580, -72.67340), ('us-ct-new-haven', 41.30830, -72.92790),
  ('us-ny-buffalo', 42.88640, -78.87840), ('us-al-birmingham', 33.51860, -86.81040), ('us-ny-rochester', 43.15660, -77.60880),
  ('us-mi-grand-rapids', 42.96340, -85.66810), ('us-az-tucson', 32.22260, -110.97470), ('us-hi-honolulu', 21.30690, -157.85830),
  ('us-ok-tulsa', 36.15400, -95.99280), ('us-ca-fresno', 36.73780, -119.78710), ('us-ma-worcester', 42.26260, -71.80230),
  ('us-ne-omaha', 41.25650, -95.93450), ('us-nm-albuquerque', 35.08440, -106.65040), ('us-sc-greenville', 34.85260, -82.39400),
  ('us-ny-albany', 42.65260, -73.75620), ('us-ca-bakersfield', 35.37330, -119.01870), ('us-tn-knoxville', 35.96060, -83.92070),
  ('us-tx-el-paso', 31.76190, -106.48500), ('us-la-baton-rouge', 30.45150, -91.18710), ('us-sc-columbia', 34.00070, -81.03480),
  ('us-sc-charleston', 32.77650, -79.93110), ('us-id-boise', 43.61500, -116.20230), ('us-wi-madison', 43.07310, -89.40120),
  ('us-ia-des-moines', 41.58680, -93.62500), ('us-ar-little-rock', 34.74650, -92.28960), ('us-wa-spokane', 47.65880, -117.42600),
  ('us-ny-syracuse', 43.04810, -76.14740), ('us-nc-asheville', 35.59510, -82.55150), ('us-vt-burlington', 44.47590, -73.21210),
  ('us-ak-anchorage', 61.21810, -149.90030), ('us-me-portland', 43.65910, -70.25680), ('us-ga-savannah', 32.08090, -81.09120),
  ('us-tn-chattanooga', 35.04560, -85.30970), ('us-ky-lexington', 38.04060, -84.50370), ('us-mi-ann-arbor', 42.28080, -83.74300),
  ('us-ga-athens', 33.95190, -83.35760), ('us-ks-wichita', 37.68720, -97.33010), ('us-nv-reno', 39.52960, -119.81380),
  ('us-ne-lincoln', 40.81360, -96.70260), ('us-tx-fort-worth', 32.75550, -97.33080), ('us-mn-st-paul', 44.95370, -93.09000),
  ('ca-on-toronto', 43.65320, -79.38320), ('ca-qc-montreal', 45.50170, -73.56730), ('ca-bc-vancouver', 49.28270, -123.12070),
  ('ca-ab-calgary', 51.04470, -114.07190), ('ca-ab-edmonton', 53.54610, -113.49380), ('ca-on-ottawa', 45.42150, -75.69720),
  ('ca-mb-winnipeg', 49.89510, -97.13840), ('ca-qc-quebec-city', 46.81390, -71.20800), ('ca-on-hamilton', 43.25570, -79.87110),
  ('ca-on-kitchener', 43.45160, -80.49250), ('ca-on-london', 42.98490, -81.24530), ('ca-ns-halifax', 44.64880, -63.57520),
  ('ca-bc-victoria', 48.42840, -123.36560), ('ca-sk-saskatoon', 52.13320, -106.67000), ('ca-sk-regina', 50.44520, -104.61890),
  ('ca-nl-st-johns', 47.56150, -52.71260)
) AS x (id, lat, lon)
WHERE cities.id = x.id;

DO $$
DECLARE missing text;
BEGIN
  SELECT string_agg(id, ', ' ORDER BY id) INTO missing FROM cities WHERE latitude IS NULL OR longitude IS NULL;
  IF missing IS NOT NULL THEN
    RAISE EXCEPTION 'cities without coordinates: %', missing;
  END IF;
END $$;

ALTER TABLE cities ALTER COLUMN latitude SET NOT NULL, ALTER COLUMN longitude SET NOT NULL;

-- Great-circle distance in km (haversine, mean Earth radius). Good to well under 1 % at these distances.
CREATE FUNCTION distance_km(p_lat1 numeric, p_lon1 numeric, p_lat2 numeric, p_lon2 numeric) RETURNS numeric
LANGUAGE sql IMMUTABLE STRICT AS $$
  SELECT 2 * 6371.0088 * asin(least(1, sqrt(
    sin(radians(p_lat2 - p_lat1) / 2) ^ 2
    + cos(radians(p_lat1)) * cos(radians(p_lat2)) * sin(radians(p_lon2 - p_lon1) / 2) ^ 2)));
$$;

CREATE FUNCTION city_distance_km(p_city_a text, p_city_b text) RETURNS numeric LANGUAGE sql STABLE STRICT AS $$
  SELECT distance_km(a.latitude, a.longitude, b.latitude, b.longitude)
  FROM cities a, cities b WHERE a.id = p_city_a AND b.id = p_city_b;
$$;

-- ---------------------------------------------------------------- the checks
CREATE TABLE hub_location_checks (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id         uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  kind            text NOT NULL CONSTRAINT hub_location_checks_kind_check CHECK (kind IN ('hub_set', 'eligibility', 'registration')),
  allocation_id   uuid REFERENCES allocations ON DELETE CASCADE,
  nearest_city_id text NOT NULL REFERENCES cities,
  nearest_city_km integer NOT NULL CHECK (nearest_city_km >= 0),   -- rounded to 10 km
  nearest_hub_km  integer CHECK (nearest_hub_km >= 0),             -- rounded to 10 km; null when the fan had no hub cities
  hub_match       boolean NOT NULL,                                 -- a hub city within hub_radius_km
  at              timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT hub_location_checks_subject_check CHECK ((kind = 'hub_set') = (allocation_id IS NULL))
);
CREATE INDEX hub_location_checks_user_idx ON hub_location_checks (user_id, at DESC);
CREATE INDEX hub_location_checks_at_idx ON hub_location_checks (at);

-- Called by the API with the device position (never stored). Returns what was recorded, or recorded = false for a
-- reading too rough to use (accuracy worse than p_max_accuracy_m) or a fan with no hub cities and kind hub_set
-- (nothing to confirm). The coordinates are bound parameters: Postgres does not log those.
CREATE FUNCTION record_hub_location(
  p_user uuid, p_kind text, p_allocation uuid, p_latitude numeric, p_longitude numeric, p_accuracy_m numeric,
  p_hub_radius_km integer DEFAULT 100, p_max_accuracy_m numeric DEFAULT 50000)
RETURNS TABLE (recorded boolean, nearest_city_id text, nearest_city_km integer, nearest_hub_km integer, hub_match boolean)
LANGUAGE plpgsql AS $$
DECLARE v_city text; v_city_km numeric; v_hub_km numeric;
BEGIN
  IF p_latitude IS NULL OR p_longitude IS NULL OR p_latitude NOT BETWEEN -90 AND 90 OR p_longitude NOT BETWEEN -180 AND 180
     OR (p_accuracy_m IS NOT NULL AND p_accuracy_m > p_max_accuracy_m) THEN
    RETURN QUERY SELECT false, NULL::text, NULL::integer, NULL::integer, NULL::boolean;
    RETURN;
  END IF;

  SELECT c.id, distance_km(p_latitude, p_longitude, c.latitude, c.longitude) INTO v_city, v_city_km
    FROM cities c WHERE c.active ORDER BY 2 LIMIT 1;
  SELECT min(distance_km(p_latitude, p_longitude, c.latitude, c.longitude)) INTO v_hub_km
    FROM user_hub_cities h JOIN cities c ON c.id = h.city_id WHERE h.user_id = p_user;

  INSERT INTO hub_location_checks (user_id, kind, allocation_id, nearest_city_id, nearest_city_km, nearest_hub_km, hub_match)
  VALUES (p_user, p_kind, p_allocation, v_city, (round(v_city_km / 10) * 10)::integer,
          (round(v_hub_km / 10) * 10)::integer, COALESCE(v_hub_km <= p_hub_radius_km, false));
  RETURN QUERY SELECT true, v_city, (round(v_city_km / 10) * 10)::integer, (round(v_hub_km / 10) * 10)::integer,
                      COALESCE(v_hub_km <= p_hub_radius_km, false);
END $$;

-- ---------------------------------------------------------------- deletion and retention
-- Same as 0054, plus the location checks.
CREATE OR REPLACE FUNCTION scrub_deleted_user_signals(p_user uuid) RETURNS void LANGUAGE sql AS $$
  DELETE FROM client_address_events WHERE user_id = p_user;
  DELETE FROM push_device_accounts  WHERE user_id = p_user;
  DELETE FROM spotify_account_links WHERE user_id = p_user;
  DELETE FROM eligibility_views     WHERE user_id = p_user;
  DELETE FROM hub_location_checks   WHERE user_id = p_user;
$$;

-- Same as 0054, plus the location checks (90 days). The old signature goes first: with both, a call without
-- arguments would be ambiguous.
DROP FUNCTION prune_client_signals(interval, interval, interval, interval, interval);
CREATE FUNCTION prune_client_signals(p_addresses interval DEFAULT interval '30 days',
                                     p_salts interval DEFAULT interval '2 days',
                                     p_devices interval DEFAULT interval '90 days',
                                     p_spotify interval DEFAULT interval '180 days',
                                     p_views interval DEFAULT interval '90 days',
                                     p_locations interval DEFAULT interval '90 days')
RETURNS TABLE (record text, removed integer) LANGUAGE plpgsql AS $$
DECLARE v integer;
BEGIN
  DELETE FROM client_address_events WHERE at < now() - p_addresses;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'client_address_events'; removed := v; RETURN NEXT;
  DELETE FROM signal_salts WHERE key LIKE 'address:%' AND created_at < now() - p_salts;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'signal_salts'; removed := v; RETURN NEXT;
  DELETE FROM push_device_accounts WHERE last_seen_at < now() - p_devices;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'push_device_accounts'; removed := v; RETURN NEXT;
  DELETE FROM spotify_account_links WHERE last_linked_at < now() - p_spotify;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'spotify_account_links'; removed := v; RETURN NEXT;
  DELETE FROM eligibility_views WHERE last_viewed_at < now() - p_views;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'eligibility_views'; removed := v; RETURN NEXT;
  DELETE FROM hub_location_checks WHERE at < now() - p_locations;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'hub_location_checks'; removed := v; RETURN NEXT;
END $$;

-- ---------------------------------------------------------------- trust
-- Same as 0039, plus hub_confirmed: a check in the last 180 days found the fan near one of their hub cities. Weighted
-- like the other parts (equal split); the trust score only breaks ties between equal raw scores in a draw.
CREATE OR REPLACE FUNCTION fan_trust_parts(p_user uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object(
    'account_age', least(1, greatest(0, extract(epoch FROM now() - u.created_at) / 86400 / 180))::numeric(5,4),
    'verified_proof', least(1, (SELECT count(*) FROM submissions s
                                 WHERE s.user_id = u.id AND s.status = 'verified' AND s.removed_at IS NULL) / 3.0)::numeric(5,4),
    'steady_listening', ((SELECT count(DISTINCT date_trunc('week', p.played_at)) FROM user_plays p
                           WHERE p.user_id = u.id AND p.played_at > now() - interval '12 weeks') / 12.0)::numeric(5,4),
    'good_standing', (1.0 / (1 + (SELECT count(*) FROM album_logs l WHERE l.user_id = u.id AND l.removed_at > now() - interval '1 year')
                               + (SELECT count(*) FROM submissions s WHERE s.user_id = u.id AND s.removed_at > now() - interval '1 year')
                               + (SELECT count(*) FROM prompt_answers a WHERE a.user_id = u.id AND a.removed_at > now() - interval '1 year')))::numeric(5,4),
    'hub_confirmed', (CASE WHEN EXISTS (SELECT 1 FROM hub_location_checks c
                                         WHERE c.user_id = u.id AND c.hub_match AND c.at > now() - interval '180 days')
                           THEN 1 ELSE 0 END)::numeric(5,4))
  FROM users u WHERE u.id = p_user;
$$;

-- ---------------------------------------------------------------- rules
INSERT INTO fraud_rules (rule_key, reason, params, description) VALUES
  ('hub_location_mismatch', 'other', '{"days": 90, "min_checks": 2, "min_km": 500, "cooldown_days": 30}',
   'min_checks or more location checks on distinct days in the last days days, each at least min_km from every hub city the fan had then'),
  ('impossible_travel', 'duplicate_account', '{"days": 30, "max_hours": 6, "min_km": 1500, "cooldown_days": 30}',
   'Two location checks within max_hours whose nearest cities are min_km or more apart, in the last days days');

-- Same as 0054, plus the two location rules.
CREATE OR REPLACE FUNCTION fraud_rule_hits(p_user uuid) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE r record; p jsonb; v_hits jsonb := '{}'; v jsonb;
BEGIN
  FOR r IN SELECT * FROM fraud_rules WHERE enabled ORDER BY rule_key LOOP
    p := r.params;
    v := NULL;
    IF r.rule_key = 'round_the_clock' THEN
      SELECT jsonb_build_object('days_over', count(*), 'most_hours', max(hours)) INTO v
        FROM (SELECT count(DISTINCT extract(hour FROM played_at AT TIME ZONE 'UTC')) AS hours
                FROM user_plays WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)
               GROUP BY (played_at AT TIME ZONE 'UTC')::date) d
       WHERE hours >= (p ->> 'min_hours')::int
      HAVING count(*) >= (p ->> 'min_days')::int;
    ELSIF r.rule_key = 'rapid_plays' THEN
      SELECT jsonb_build_object('plays', count(*), 'rapid', count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric),
                                'share', round(count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric)::numeric / count(*), 3)) INTO v
        FROM (SELECT extract(epoch FROM played_at - lag(played_at) OVER (ORDER BY played_at)) AS gap
                FROM user_plays WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)) g
      HAVING count(*) >= (p ->> 'min_plays')::int
         AND count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric) >= (p ->> 'min_share')::numeric * count(*);
    ELSIF r.rule_key = 'looping' THEN
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'plays', total, 'top_share', round(top / total, 3))) INTO v
        FROM (SELECT t.artist_id, sum(t.n)::numeric AS total,
                     (SELECT sum(n) FROM (SELECT count(*) AS n FROM user_plays x
                                           WHERE x.user_id = p_user AND x.artist_id = t.artist_id
                                             AND x.played_at >= now() - make_interval(days => (p ->> 'days')::int)
                                           GROUP BY x.spotify_track_id ORDER BY 1 DESC LIMIT (p ->> 'top_tracks')::int) z)::numeric AS top
                FROM (SELECT artist_id, spotify_track_id, count(*) AS n FROM user_plays
                       WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)
                       GROUP BY 1, 2) t
               GROUP BY t.artist_id) a
       WHERE total >= (p ->> 'min_plays')::int AND top >= (p ->> 'min_share')::numeric * total
         AND (SELECT count(*) FROM tracks tr JOIN albums al ON al.id = tr.album_id WHERE al.artist_id = a.artist_id)
             >= (p ->> 'min_catalog_tracks')::int;
    ELSIF r.rule_key = 'listening_spike' THEN
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'recent_plays', recent,
                                          'weekly_average_before', round(before / (p ->> 'baseline_weeks')::numeric, 1))) INTO v
        FROM (SELECT artist_id,
                     count(*) FILTER (WHERE played_at >= now() - make_interval(days => (p ->> 'days')::int)) AS recent,
                     count(*) FILTER (WHERE played_at < now() - make_interval(days => (p ->> 'days')::int)) AS before
                FROM user_plays
               WHERE user_id = p_user
                 AND played_at >= now() - make_interval(days => (p ->> 'days')::int + 7 * (p ->> 'baseline_weeks')::int)
               GROUP BY artist_id) s
       WHERE recent >= (p ->> 'min_plays')::int
         AND recent >= (p ->> 'ratio')::numeric * greatest(before / (p ->> 'baseline_weeks')::numeric, 1)
         AND EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                      WHERE e.artist_id = s.artist_id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL
                        AND a.registration_closes_at > now()
                        AND a.registration_opens_at < now() + make_interval(days => (p ->> 'lead_days')::int));
    ELSIF r.rule_key = 'burst_engagement' THEN
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'saves', burst, 'window_hours', (p ->> 'window_hours')::int,
                                          'window_start', window_start, 'all_saves', total,
                                          'kinds', jsonb_build_object('liked', liked, 'playlist', playlist, 'album', album, 'follow', follow),
                                          'after_signup', after_signup, 'fan_tickets', fan_tickets)) INTO v
        FROM (SELECT DISTINCT ON (w.artist_id) w.artist_id, w.burst, w.window_start, w.total, w.liked, w.playlist, w.album, w.follow,
                     w.window_start < u.created_at + make_interval(days => (p ->> 'signup_days')::int) AS after_signup,
                     EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                              WHERE e.artist_id = w.artist_id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL
                                AND a.registration_closes_at > now()
                                AND a.registration_opens_at < now() + make_interval(days => (p ->> 'lead_days')::int)) AS fan_tickets
                FROM (SELECT artist_id, saved_at AS window_start, countable,
                             count(*) FILTER (WHERE countable) OVER win AS burst,
                             count(*) FILTER (WHERE countable AND kind = 'liked') OVER win AS liked,
                             count(*) FILTER (WHERE countable AND kind = 'playlist') OVER win AS playlist,
                             count(*) FILTER (WHERE countable AND kind = 'album') OVER win AS album,
                             count(*) FILTER (WHERE countable AND kind = 'follow') OVER win AS follow,
                             count(*) OVER (PARTITION BY artist_id) AS total
                        FROM (SELECT al.artist_id, l.saved_at, l.source AS kind, NOT l.saved_at_baseline AS countable
                                FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
                               WHERE l.user_id = p_user AND l.saved_at IS NOT NULL
                              UNION ALL
                              SELECT al.artist_id, s.saved_at, 'album', true
                                FROM user_album_saves s JOIN albums al ON al.id = s.album_id
                               WHERE s.user_id = p_user AND s.saved_at IS NOT NULL
                              UNION ALL
                              SELECT g.artist_id, g.followed_since, 'follow', NOT g.followed_since_baseline
                                FROM user_artist_signals g
                               WHERE g.user_id = p_user AND g.followed_since IS NOT NULL) saves
                      WINDOW win AS (PARTITION BY artist_id ORDER BY saved_at
                                     RANGE BETWEEN CURRENT ROW AND make_interval(hours => (p ->> 'window_hours')::int) FOLLOWING)) w
                JOIN users u ON u.id = p_user
               WHERE w.countable
                 AND w.window_start >= now() - make_interval(days => (p ->> 'days')::int)
                 AND w.burst >= (p ->> 'min_saves')::int
                 AND w.burst >= (p ->> 'min_share')::numeric * w.total
               ORDER BY w.artist_id, w.burst DESC, w.window_start) b
       WHERE after_signup OR fan_tickets;
    ELSIF r.rule_key = 'shared_device' THEN
      SELECT jsonb_build_object('devices', count(*), 'most_accounts', max(n), 'days', (p ->> 'days')::int) INTO v
        FROM (SELECT d.token_hash, count(DISTINCT o.user_id) AS n
                FROM push_device_accounts d
                JOIN push_device_accounts o ON o.token_hash = d.token_hash
                 AND o.last_seen_at >= now() - make_interval(days => (p ->> 'days')::int)
               WHERE d.user_id = p_user AND d.last_seen_at >= now() - make_interval(days => (p ->> 'days')::int)
               GROUP BY d.token_hash
              HAVING count(DISTINCT o.user_id) >= (p ->> 'min_accounts')::int) s
      HAVING count(*) > 0;
    ELSIF r.rule_key = 'shared_address_signups' THEN
      SELECT jsonb_build_object('accounts', count(DISTINCT o.user_id), 'day', me.day) INTO v
        FROM client_address_events me
        JOIN client_address_events o ON o.address_hash = me.address_hash AND o.day = me.day AND o.kind = 'signup'
       WHERE me.user_id = p_user AND me.kind = 'signup' AND me.at >= now() - make_interval(days => (p ->> 'days')::int)
       GROUP BY me.day
      HAVING count(DISTINCT o.user_id) >= (p ->> 'min_accounts')::int
       ORDER BY count(DISTINCT o.user_id) DESC LIMIT 1;
    ELSIF r.rule_key = 'shared_address_registrations' THEN
      SELECT jsonb_agg(jsonb_build_object('allocation_id', allocation_id, 'accounts', n, 'day', day)) INTO v
        FROM (SELECT me.allocation_id, me.day, count(DISTINCT o.user_id) AS n
                FROM client_address_events me
                JOIN client_address_events o ON o.address_hash = me.address_hash AND o.day = me.day
                 AND o.kind = 'registration' AND o.allocation_id = me.allocation_id
               WHERE me.user_id = p_user AND me.kind = 'registration'
                 AND me.at >= now() - make_interval(days => (p ->> 'days')::int)
               GROUP BY me.allocation_id, me.day
              HAVING count(DISTINCT o.user_id) >= (p ->> 'min_accounts')::int) s;
    ELSIF r.rule_key = 'spotify_account_reuse' THEN
      SELECT jsonb_build_object('other_accounts', other_accounts, 'accounts_connected', accounts_connected, 'days', (p ->> 'days')::int) INTO v
        FROM (SELECT count(DISTINCT me.account_hash) AS accounts_connected,
                     COALESCE(max((SELECT count(DISTINCT o.user_id) FROM spotify_account_links o
                                    WHERE o.account_hash = me.account_hash AND o.user_id <> p_user
                                      AND o.last_linked_at >= now() - make_interval(days => (p ->> 'days')::int))), 0) AS other_accounts
                FROM spotify_account_links me
               WHERE me.user_id = p_user AND me.last_linked_at >= now() - make_interval(days => (p ->> 'days')::int)) s
       WHERE other_accounts >= (p ->> 'min_other_accounts')::int OR accounts_connected >= (p ->> 'min_accounts_connected')::int;
    ELSIF r.rule_key = 'headless_registration' THEN
      SELECT jsonb_build_object('registrations', count(*), 'days', (p ->> 'days')::int) INTO v
        FROM eligibility_audit a
       WHERE a.user_id = p_user AND a.action = 'registered' AND a.at >= now() - make_interval(days => (p ->> 'days')::int)
         AND NOT EXISTS (SELECT 1 FROM eligibility_views ev
                          WHERE ev.user_id = p_user AND ev.allocation_id = a.allocation_id AND ev.first_viewed_at < a.at)
      HAVING count(*) >= (p ->> 'min_registrations')::int;
    ELSIF r.rule_key = 'instant_registration' THEN
      SELECT jsonb_build_object('registrations', count(*), 'fastest_minutes', round(min(extract(epoch FROM a.at - u.created_at)) / 60, 1),
                                'max_minutes', (p ->> 'max_minutes')::int) INTO v
        FROM eligibility_audit a JOIN users u ON u.id = a.user_id
       WHERE a.user_id = p_user AND a.action = 'registered' AND a.at >= now() - make_interval(days => (p ->> 'days')::int)
         AND a.at < u.created_at + make_interval(mins => (p ->> 'max_minutes')::int)
      HAVING count(*) >= (p ->> 'min_registrations')::int;
    ELSIF r.rule_key = 'eligibility_polling' THEN
      SELECT jsonb_agg(jsonb_build_object('allocation_id', allocation_id, 'day', day, 'checks', views)) INTO v
        FROM eligibility_views
       WHERE user_id = p_user AND day >= (now() AT TIME ZONE 'UTC')::date - (p ->> 'days')::int
         AND views >= (p ->> 'min_checks')::int;
    ELSIF r.rule_key = 'hub_location_mismatch' THEN
      -- Counts and distances only: how many checks, on how many days, and how far the nearest hub was at best.
      SELECT jsonb_build_object('checks', count(*), 'days', count(DISTINCT (at AT TIME ZONE 'UTC')::date),
                                'closest_hub_km', min(nearest_hub_km), 'min_km', (p ->> 'min_km')::int) INTO v
        FROM hub_location_checks
       WHERE user_id = p_user AND at >= now() - make_interval(days => (p ->> 'days')::int)
         AND nearest_hub_km >= (p ->> 'min_km')::int
      HAVING count(DISTINCT (at AT TIME ZONE 'UTC')::date) >= (p ->> 'min_checks')::int;
    ELSIF r.rule_key = 'impossible_travel' THEN
      -- Two checks close in time whose nearest cities are far apart. The distance is between the cities (the fan's own
      -- position is never stored).
      SELECT jsonb_build_object('pairs', count(*), 'farthest_km', max(km), 'shortest_hours', min(hours),
                                'max_hours', (p ->> 'max_hours')::int, 'min_km', (p ->> 'min_km')::int) INTO v
        FROM (SELECT round(city_distance_km(a.nearest_city_id, b.nearest_city_id)) AS km,
                     round(extract(epoch FROM b.at - a.at) / 3600, 1) AS hours
                FROM hub_location_checks a
                JOIN hub_location_checks b ON b.user_id = a.user_id AND b.id > a.id
                 AND b.at BETWEEN a.at AND a.at + make_interval(hours => (p ->> 'max_hours')::int)
               WHERE a.user_id = p_user AND a.at >= now() - make_interval(days => (p ->> 'days')::int)
                 AND a.nearest_city_id <> b.nearest_city_id) x
       WHERE km >= (p ->> 'min_km')::int
      HAVING count(*) > 0;
    END IF;
    -- A rule whose flag staff cleared recently stays quiet for its cooldown.
    IF v IS NOT NULL AND NOT EXISTS (
         SELECT 1 FROM fraud_flags f
          WHERE f.user_id = p_user AND f.source = 'rule' AND f.status = 'cleared'
            AND f.evidence -> 'rules' ? r.rule_key
            AND f.decided_at > now() - make_interval(days => COALESCE((p ->> 'cooldown_days')::int, 30))) THEN
      v_hits := v_hits || jsonb_build_object(r.rule_key, v);
    END IF;
  END LOOP;
  RETURN v_hits;
END $$;

-- Same as 0054, plus fans with a location check in the window.
CREATE OR REPLACE FUNCTION run_fraud_detection(p_since interval DEFAULT interval '1 day') RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT user_id FROM user_plays WHERE played_at >= now() - p_since
           UNION
           SELECT user_id FROM user_track_likes WHERE saved_at >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM user_album_saves WHERE saved_at >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM user_artist_signals WHERE followed_since >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM eligibility_audit WHERE action = 'registered' AND at >= now() - p_since
           UNION
           SELECT user_id FROM eligibility_views WHERE last_viewed_at >= now() - p_since
           UNION
           SELECT user_id FROM hub_location_checks WHERE at >= now() - p_since
           UNION
           SELECT o.user_id FROM client_address_events e
           JOIN client_address_events o ON o.address_hash = e.address_hash AND o.day = e.day
           WHERE e.at >= now() - p_since
           UNION
           SELECT o.user_id FROM push_device_accounts d JOIN push_device_accounts o ON o.token_hash = d.token_hash
           WHERE d.last_seen_at >= now() - p_since
           UNION
           SELECT o.user_id FROM spotify_account_links l JOIN spotify_account_links o ON o.account_hash = l.account_hash
           WHERE l.last_linked_at >= now() - p_since LOOP
    IF detect_fraud(r.user_id) IS NOT NULL THEN
      v_count := v_count + 1;
    END IF;
  END LOOP;
  RETURN v_count;
END $$;
