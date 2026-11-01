-- Bot and shared-device detection (Phase 10, 2026-11-01; docs/security/bot-and-shared-device-detection.md).
--
-- Until now detection read only the play ledger and the synced library, so linked accounts (one person, many
-- accounts) and clients that aren't the app were for staff to spot. This migration keeps four small, short-lived
-- signals the API already sees, and adds seven rules over them to the fraud.detect sweep:
-- - client_address_events: the client address at sign-up and at each registration, as sha256(daily salt || address)
--   with the UTC day. The salt (signal_salts, 'address:<day>') is random, made in the database, and deleted after two
--   days, after which no hash can be matched to an address, even by us; hashes of different days never match each
--   other. Rows live 30 days.
-- - push_device_accounts: which accounts registered the same push token (sha256 of the token; the token itself is in
--   push_devices and moves to whoever signs in last, so this is the history that was lost). 90 days after last seen.
-- - spotify_account_links: which accounts connected the same Spotify account (sha256(fixed salt || spotify_user_id);
--   fixed, because reuse is only visible across months). 180 days after last linked.
-- - eligibility_views: one row per fan, allocation, and UTC day with how often the fan checked their standing and the
--   first and last time. 90 days.
-- Every row goes at once when the account is deleted. No raw address is stored anywhere; it reaches one function as a
-- bound parameter and is hashed there. Nothing here is in the fan's data download (DATA_INVENTORY.md).
--
-- The rules (thresholds in fraud_rules, provisional; evidence holds counts, days, and allocation ids only):
-- - shared_device (shared_device): a push device the fan used in the last days days was registered by min_accounts
--   or more accounts in that time.
-- - shared_address_signups (duplicate_account): min_accounts or more accounts created from the fan's sign-up address on
--   the same UTC day, in the last days days.
-- - shared_address_registrations (duplicate_account): min_accounts or more accounts registered for the same allocation
--   from the same address on the same day, in the last days days.
-- - spotify_account_reuse (duplicate_account): the fan's Spotify account was linked to min_other_accounts or more
--   other fan accounts in the last days days, or the fan connected min_accounts_connected or more Spotify accounts.
-- - headless_registration (bot): min_registrations or more registrations in days days with no eligibility check of
--   that allocation before registering; the app always checks first.
-- - instant_registration (bot): min_registrations or more registrations within max_minutes of creating the account.
-- - eligibility_polling (bot): min_checks or more eligibility checks of one allocation in one UTC day, in the last
--   days days.
-- As before (0043): an open rule flag only blocks registering, a held fan isn't flagged again, a cleared rule waits
-- cooldown_days, and the flag is named by the strongest reason. The sweep also checks every account sharing a device,
-- address-day, or Spotify account with a fan seen in the window, so a cluster is reviewed as a whole.

-- ---------------------------------------------------------------- salts and hashes
CREATE TABLE signal_salts (
  key        text PRIMARY KEY,   -- 'address:<UTC day>' (deleted after two days) or 'spotify_account' (kept)
  salt       bytea NOT NULL CHECK (octet_length(salt) >= 32),
  created_at timestamptz NOT NULL DEFAULT now()
);

-- The salt for a key, made on first use. Two random UUIDs: 244 random bits, core Postgres only (no pgcrypto).
CREATE FUNCTION signal_salt(p_key text) RETURNS bytea LANGUAGE plpgsql AS $$
DECLARE v bytea;
BEGIN
  SELECT salt INTO v FROM signal_salts WHERE key = p_key;
  IF NOT FOUND THEN
    INSERT INTO signal_salts (key, salt) VALUES (p_key, decode(replace(gen_random_uuid()::text || gen_random_uuid()::text, '-', ''), 'hex'))
    ON CONFLICT (key) DO NOTHING;
    SELECT salt INTO v FROM signal_salts WHERE key = p_key;
  END IF;
  RETURN v;
END $$;

-- Today's pseudonym for a client address. Matches other requests from the same address today and nothing else.
CREATE FUNCTION client_address_hash(p_address text) RETURNS bytea LANGUAGE sql AS $$
  SELECT sha256(signal_salt('address:' || (now() AT TIME ZONE 'UTC')::date) || convert_to(p_address, 'UTF8'));
$$;

CREATE FUNCTION spotify_account_hash(p_spotify_user_id text) RETURNS bytea LANGUAGE sql AS $$
  SELECT sha256(signal_salt('spotify_account') || convert_to(p_spotify_user_id, 'UTF8'));
$$;

-- ---------------------------------------------------------------- client address events
CREATE TABLE client_address_events (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id       uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  kind          text NOT NULL CONSTRAINT client_address_events_kind_check CHECK (kind IN ('signup', 'registration')),
  allocation_id uuid REFERENCES allocations ON DELETE CASCADE,
  day           date NOT NULL,
  address_hash  bytea NOT NULL CHECK (octet_length(address_hash) = 32),
  at            timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT client_address_events_subject_check CHECK ((kind = 'registration') = (allocation_id IS NOT NULL))
);
CREATE INDEX client_address_events_hash_idx ON client_address_events (address_hash, day, kind);
CREATE INDEX client_address_events_user_idx ON client_address_events (user_id, at);
CREATE INDEX client_address_events_at_idx ON client_address_events (at);

-- Called by the API with the client address (never stored). A request with no known address records nothing.
CREATE FUNCTION record_client_address(p_user uuid, p_kind text, p_allocation uuid, p_address text) RETURNS void
LANGUAGE sql AS $$
  INSERT INTO client_address_events (user_id, kind, allocation_id, day, address_hash)
  SELECT p_user, p_kind, p_allocation, (now() AT TIME ZONE 'UTC')::date, client_address_hash(p_address)
   WHERE p_address IS NOT NULL AND p_address <> '';
$$;

-- ---------------------------------------------------------------- push device history
CREATE TABLE push_device_accounts (
  token_hash    bytea NOT NULL CHECK (octet_length(token_hash) = 32),
  user_id       uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  first_seen_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (token_hash, user_id),
  CHECK (last_seen_at >= first_seen_at)
);
CREATE INDEX push_device_accounts_user_idx ON push_device_accounts (user_id);
CREATE INDEX push_device_accounts_seen_idx ON push_device_accounts (last_seen_at);

-- greatest(): the same request sent twice at once runs as two transactions whose now() differ; the earlier one may
-- finish second and must not move the time backwards (the same in the two upserts below).
CREATE FUNCTION trg_push_device_account() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO push_device_accounts (token_hash, user_id) VALUES (sha256(convert_to(NEW.token, 'UTF8')), NEW.user_id)
  ON CONFLICT (token_hash, user_id) DO UPDATE SET last_seen_at = greatest(push_device_accounts.last_seen_at, now());
  RETURN NULL;
END $$;
CREATE TRIGGER trg_push_device_account AFTER INSERT OR UPDATE OF user_id, last_seen_at ON push_devices
  FOR EACH ROW EXECUTE FUNCTION trg_push_device_account();

-- ---------------------------------------------------------------- Spotify account history
CREATE TABLE spotify_account_links (
  account_hash   bytea NOT NULL CHECK (octet_length(account_hash) = 32),
  user_id        uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  first_linked_at timestamptz NOT NULL DEFAULT now(),
  last_linked_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (account_hash, user_id),
  CHECK (last_linked_at >= first_linked_at)
);
CREATE INDEX spotify_account_links_user_idx ON spotify_account_links (user_id);
CREATE INDEX spotify_account_links_linked_idx ON spotify_account_links (last_linked_at);

CREATE FUNCTION trg_spotify_account_link() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO spotify_account_links (account_hash, user_id) VALUES (spotify_account_hash(NEW.spotify_user_id), NEW.user_id)
  ON CONFLICT (account_hash, user_id) DO UPDATE SET last_linked_at = greatest(spotify_account_links.last_linked_at, now());
  RETURN NULL;
END $$;
CREATE TRIGGER trg_spotify_account_link AFTER INSERT OR UPDATE OF spotify_user_id ON spotify_connections
  FOR EACH ROW EXECUTE FUNCTION trg_spotify_account_link();
-- Connections that exist already: linked now (their connected_at is kept as first_linked_at).
INSERT INTO spotify_account_links (account_hash, user_id, first_linked_at, last_linked_at)
SELECT spotify_account_hash(spotify_user_id), user_id, connected_at, greatest(connected_at, now()) FROM spotify_connections;

-- ---------------------------------------------------------------- eligibility checks
CREATE TABLE eligibility_views (
  user_id         uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  allocation_id   uuid NOT NULL REFERENCES allocations ON DELETE CASCADE,
  day             date NOT NULL,
  views           integer NOT NULL DEFAULT 1 CHECK (views > 0),
  first_viewed_at timestamptz NOT NULL DEFAULT now(),
  last_viewed_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, allocation_id, day),
  CHECK (last_viewed_at >= first_viewed_at)
);
CREATE INDEX eligibility_views_last_idx ON eligibility_views (last_viewed_at);

CREATE FUNCTION record_eligibility_view(p_user uuid, p_allocation uuid) RETURNS void LANGUAGE sql AS $$
  INSERT INTO eligibility_views (user_id, allocation_id, day) VALUES (p_user, p_allocation, (now() AT TIME ZONE 'UTC')::date)
  ON CONFLICT (user_id, allocation_id, day) DO UPDATE
    SET views = eligibility_views.views + 1, last_viewed_at = greatest(eligibility_views.last_viewed_at, now());
$$;

-- ---------------------------------------------------------------- deletion and retention
-- Deleting the account removes every signal at once (the FKs cascade on a hard delete too; this covers the soft delete).
CREATE FUNCTION scrub_deleted_user_signals(p_user uuid) RETURNS void LANGUAGE sql AS $$
  DELETE FROM client_address_events WHERE user_id = p_user;
  DELETE FROM push_device_accounts  WHERE user_id = p_user;
  DELETE FROM spotify_account_links WHERE user_id = p_user;
  DELETE FROM eligibility_views     WHERE user_id = p_user;
$$;
CREATE FUNCTION trg_user_delete_signals() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM scrub_deleted_user_signals(NEW.id);
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_delete_signals AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_delete_signals();
SELECT scrub_deleted_user_signals(id) FROM users WHERE deleted_at IS NOT NULL;

-- Run daily by privacy.retention, next to prune_expired_records(). Safe to repeat.
CREATE FUNCTION prune_client_signals(p_addresses interval DEFAULT interval '30 days',
                                     p_salts interval DEFAULT interval '2 days',
                                     p_devices interval DEFAULT interval '90 days',
                                     p_spotify interval DEFAULT interval '180 days',
                                     p_views interval DEFAULT interval '90 days')
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
END $$;

-- ---------------------------------------------------------------- rules
INSERT INTO fraud_rules (rule_key, reason, params, description) VALUES
  ('shared_device', 'shared_device', '{"days": 90, "min_accounts": 3, "cooldown_days": 30}',
   'A push device the fan used in the last days days was registered by min_accounts or more accounts in that time'),
  ('shared_address_signups', 'duplicate_account', '{"days": 30, "min_accounts": 5, "cooldown_days": 30}',
   'min_accounts or more accounts created from the fan''s sign-up address on the same UTC day, in the last days days'),
  ('shared_address_registrations', 'duplicate_account', '{"days": 30, "min_accounts": 5, "cooldown_days": 30}',
   'min_accounts or more accounts registered for the same allocation from the same address on the same UTC day, in the last days days'),
  ('spotify_account_reuse', 'duplicate_account', '{"days": 180, "min_other_accounts": 2, "min_accounts_connected": 3, "cooldown_days": 30}',
   'The fan''s Spotify account was linked to min_other_accounts or more other fan accounts in days days, or the fan connected min_accounts_connected or more Spotify accounts'),
  ('headless_registration', 'bot', '{"days": 30, "min_registrations": 2, "cooldown_days": 30}',
   'min_registrations or more registrations in days days with no eligibility check of that allocation beforehand (the app always checks first)'),
  ('instant_registration', 'bot', '{"days": 30, "max_minutes": 10, "min_registrations": 2, "cooldown_days": 30}',
   'min_registrations or more registrations within max_minutes of creating the account, in the last days days'),
  ('eligibility_polling', 'bot', '{"days": 7, "min_checks": 300, "cooldown_days": 30}',
   'min_checks or more eligibility checks of one allocation in one UTC day, in the last days days');

-- Same as 0049, plus the seven rules above.
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
      -- Every countable save (liked track, playlist add, album save, follow; not a baseline row, whose date is only
      -- when the sync first looked) opens a window; the densest window per artist is the burst. all_saves counts
      -- baseline rows too: they are the fan's saves of the artist all the same.
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
      -- Counts only: how many of the fan's devices are shared, and the most accounts on one of them.
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

-- Same as 0048, plus fans who signed up, registered, checked eligibility, registered a push device, or connected
-- Spotify in the window, and every account sharing a device, address-day, or Spotify account with one of them.
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
