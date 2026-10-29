-- Automatic fraud detection (Phase 10, 2026-10-29).
--
-- Rules over the play ledger (user_plays: tracked artists only, polled hourly, at most 50 plays per poll) raise fraud
-- flags with source 'rule' for staff to review. There's no device or network data, so shared devices and second
-- accounts are still for staff to spot.
--
-- What a rule flag does (decided 2026-10-29): while open it only blocks registering for fan tickets
-- (registration_blocker() answers under_review, as for any open flag). It does not withdraw the fan from a draw or skip
-- them for a returned seat; that full hold starts only when staff uphold it. Staff flags still hold fully while open.
--
-- The rules and their thresholds are data (fraud_rules), like scoring_rules; the starting values are provisional
-- (PROJECT_TODO.md). The sweep (fraud.detect, every 6 hours) checks fans with plays in the last day:
-- - round_the_clock (bot): plays in at least min_hours distinct hours of a UTC day, on at least min_days of the last
--   days days. People sleep.
-- - rapid_plays (bot): of at least min_plays plays in the last days days, at least min_share started under
--   max_gap_seconds after the previous one. Spotify counts a play after 30 seconds; streaming bots play just past that.
-- - looping (streaming_farm): at least min_plays plays of one artist in the last days days, at least min_share of them
--   on their top_tracks tracks, for an artist with at least min_catalog_tracks tracks (a band with three songs out is
--   looped by real fans).
-- - listening_spike (streaming_farm): at least min_plays plays of one artist in the last days days, at least ratio times
--   the fan's weekly average over the baseline_weeks before, while the artist has fan tickets open or opening within
--   lead_days days. Gaming is aimed at registration.
-- A fan with an open or upheld flag isn't flagged again; a rule whose flag staff cleared doesn't fire again for
-- cooldown_days. One flag per sweep, with every rule that fired in its evidence.

CREATE TABLE fraud_rules (
  rule_key    text PRIMARY KEY,
  reason      text NOT NULL CHECK (reason IN ('streaming_farm', 'bot', 'shared_device', 'duplicate_account', 'other')),
  enabled     boolean NOT NULL DEFAULT true,
  params      jsonb NOT NULL CHECK (jsonb_typeof(params) = 'object'),
  description text NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO fraud_rules (rule_key, reason, params, description) VALUES
  ('round_the_clock', 'bot', '{"days": 14, "min_hours": 20, "min_days": 3, "cooldown_days": 30}',
   'Plays in at least min_hours distinct hours of a UTC day, on at least min_days of the last days days'),
  ('rapid_plays', 'bot', '{"days": 7, "min_plays": 100, "max_gap_seconds": 40, "min_share": 0.7, "cooldown_days": 30}',
   'At least min_share of min_plays or more plays started under max_gap_seconds after the previous play'),
  ('looping', 'streaming_farm', '{"days": 7, "min_plays": 300, "top_tracks": 3, "min_share": 0.9, "min_catalog_tracks": 10, "cooldown_days": 30}',
   'At least min_plays plays of one artist, min_share of them on its top_tracks tracks, for an artist with min_catalog_tracks tracks or more'),
  ('listening_spike', 'streaming_farm', '{"days": 7, "min_plays": 300, "ratio": 10, "baseline_weeks": 8, "lead_days": 14, "cooldown_days": 30}',
   'At least min_plays plays of an artist in days days and ratio times the weekly average before, while the artist has fan tickets open or opening within lead_days');

-- The rules that fire for a fan now, with what was measured: {rule_key: {...}}. Empty when none.
CREATE FUNCTION fraud_rule_hits(p_user uuid) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
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

-- Raises a rule flag for the fan if any rule fires and nothing holds them already. Returns the flag id, or null.
-- The fan's account row is locked so two sweeps can't race (the one-open-flag index would refuse the second anyway).
CREATE FUNCTION detect_fraud(p_user uuid) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_hits jsonb; v_reason text; v_id uuid;
BEGIN
  PERFORM 1 FROM users WHERE id = p_user AND deleted_at IS NULL AND NOT is_staff FOR NO KEY UPDATE;
  IF NOT FOUND OR fan_on_hold(p_user) THEN RETURN NULL; END IF;
  v_hits := fraud_rule_hits(p_user);
  IF v_hits = '{}' THEN RETURN NULL; END IF;
  -- Bot rules name the flag before farm rules: they're the stronger signal.
  SELECT reason INTO v_reason FROM fraud_rules WHERE v_hits ? rule_key ORDER BY reason = 'bot' DESC, rule_key LIMIT 1;
  INSERT INTO fraud_flags (user_id, reason, evidence, source)
  VALUES (p_user, v_reason, jsonb_build_object('rules', v_hits, 'checked_at', now()), 'rule')
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- The sweep: every fan with plays since p_since. Returns how many flags were raised.
CREATE FUNCTION run_fraud_detection(p_since interval DEFAULT interval '1 day') RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT DISTINCT user_id FROM user_plays WHERE played_at >= now() - p_since LOOP
    IF detect_fraud(r.user_id) IS NOT NULL THEN
      v_count := v_count + 1;
    END IF;
  END LOOP;
  RETURN v_count;
END $$;

-- The full hold (held out of draws and returned seats): an upheld flag, or an open one staff raised. An open rule flag
-- only blocks registering (fan_on_hold(), unchanged).
CREATE FUNCTION fan_full_hold_flag(p_user uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
  SELECT id FROM fraud_flags
   WHERE user_id = p_user AND (status = 'upheld' OR (status = 'open' AND source = 'staff'))
   ORDER BY raised_at DESC LIMIT 1;
$$;

-- Same as 0042, with the full hold: an open rule flag no longer withdraws the fan from the draw.
CREATE OR REPLACE FUNCTION run_allocation_selection(p_allocation uuid, p_by uuid) RETURNS allocation_selections
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

  FOR r IN SELECT reg.id, reg.user_id, f.id AS flag_id, f.reason, f.status
             FROM allocation_registrations reg
             JOIN LATERAL (SELECT * FROM fraud_flags ff
                            WHERE ff.user_id = reg.user_id AND (ff.status = 'upheld' OR (ff.status = 'open' AND ff.source = 'staff'))
                            ORDER BY ff.raised_at DESC LIMIT 1 FOR SHARE) f ON true
            WHERE reg.allocation_id = p_allocation AND reg.withdrawn_at IS NULL
            ORDER BY reg.id LOOP
    UPDATE allocation_registrations SET withdrawn_at = clock_timestamp(), withdrawn_reason = 'fraud_hold' WHERE id = r.id;
    INSERT INTO eligibility_audit (user_id, action, reason, allocation_id, registration_id, flag_id, inputs)
    VALUES (r.user_id, 'draw_held_out', r.reason, p_allocation, r.id, r.flag_id, jsonb_build_object('flag_status', r.status));
  END LOOP;

  PERFORM set_config('fandom.selecting', p_allocation::text, true);
  INSERT INTO allocation_trust_snapshots (allocation_id, registration_id, trust)
  SELECT p_allocation, reg.id, parts_average(fan_trust_parts(reg.user_id))
    FROM allocation_registrations reg
   WHERE reg.allocation_id = p_allocation AND reg.withdrawn_at IS NULL;

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

-- Same as 0042, with the full hold: an open rule flag no longer skips the fan for a returned seat.
CREATE OR REPLACE FUNCTION backfill_allocation(p_allocation uuid) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE s allocation_selections; r record; v_free integer; v_seats smallint; v_added integer := 0; v_flag uuid;
BEGIN
  SELECT * INTO s FROM allocation_selections WHERE allocation_id = p_allocation FOR UPDATE;
  IF NOT FOUND THEN RETURN 0; END IF;   -- no draw yet: nothing to hand on
  v_free := s.tickets - s.seats_assigned;
  PERFORM set_config('fandom.backfilling', p_allocation::text, true);
  FOR r IN SELECT reg.id, reg.user_id, reg.plus_one FROM allocation_registrations reg
            WHERE reg.allocation_id = p_allocation AND reg.withdrawn_at IS NULL AND reg.outcome = 'not_selected'
            ORDER BY reg.selection_rank LOOP
    EXIT WHEN v_free <= 0;
    SELECT id INTO v_flag FROM fraud_flags
     WHERE user_id = r.user_id AND (status = 'upheld' OR (status = 'open' AND source = 'staff'))
     ORDER BY raised_at DESC LIMIT 1 FOR SHARE;
    IF FOUND THEN
      INSERT INTO eligibility_audit (user_id, action, allocation_id, registration_id, flag_id)
      SELECT r.user_id, 'backfill_skipped', p_allocation, r.id, v_flag
       WHERE NOT EXISTS (SELECT 1 FROM eligibility_audit x WHERE x.action = 'backfill_skipped' AND x.registration_id = r.id
                           AND x.flag_id = v_flag);
      CONTINUE;
    END IF;
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
