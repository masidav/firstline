-- Two overall scores per fan, across all artists (decided 2026-10-28; weights and caps provisional, PROJECT_TODO.md):
-- 1. Trust, behind the scenes: used only to break ties in fan ticket draws (equal snapshot raw scores), never to lower
--    a fan's chances otherwise. Parts, weighted equally: account age, verified proof, steady listening, good standing.
-- 2. Fan level, visible: levels 1 to 5 from activity across the app. Parts, weighted equally: fandom (best scores),
--    albums logged, verified proof, trivia answered right, prompts answered. It never decides tickets.
-- Each part is 0 to 1 and saturates at a cap. "This or that" and blind rankings join both with a very small share
-- once they exist.

-- ---------------------------------------------------------------- parts
-- Trust parts for one fan, each 0..1.
CREATE FUNCTION fan_trust_parts(p_user uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object(
    -- Older accounts are harder to fake in bulk: full at 180 days.
    'account_age', least(1, greatest(0, extract(epoch FROM now() - u.created_at) / 86400 / 180))::numeric(5,4),
    -- Proof a person checked: full at 3 verified submissions (not taken down).
    'verified_proof', least(1, (SELECT count(*) FROM submissions s
                                 WHERE s.user_id = u.id AND s.status = 'verified' AND s.removed_at IS NULL) / 3.0)::numeric(5,4),
    -- Listening spread over time rather than in a burst: weeks with plays, of the last 12.
    'steady_listening', ((SELECT count(DISTINCT date_trunc('week', p.played_at)) FROM user_plays p
                           WHERE p.user_id = u.id AND p.played_at > now() - interval '12 weeks') / 12.0)::numeric(5,4),
    -- Good standing: content of theirs staff took down in the last year lowers it.
    'good_standing', (1.0 / (1 + (SELECT count(*) FROM album_logs l WHERE l.user_id = u.id AND l.removed_at > now() - interval '1 year')
                               + (SELECT count(*) FROM submissions s WHERE s.user_id = u.id AND s.removed_at > now() - interval '1 year')
                               + (SELECT count(*) FROM prompt_answers a WHERE a.user_id = u.id AND a.removed_at > now() - interval '1 year')))::numeric(5,4))
  FROM users u WHERE u.id = p_user;
$$;

-- Activity parts for one fan, each 0..1.
CREATE FUNCTION fan_activity_parts(p_user uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object(
    -- The fan's three best percentiles, averaged (missing ones count as 0).
    'fandom', (COALESCE((SELECT sum(percentile) FROM (SELECT percentile FROM user_artist_scores sc
                          JOIN artists a ON a.id = sc.artist_id AND a.status = 'active'
                          WHERE sc.user_id = u.id ORDER BY percentile DESC LIMIT 3) best), 0) / 300)::numeric(5,4),
    -- Albums logged: full at 50, growing fastest at first.
    'albums_logged', least(1, ln(1 + (SELECT count(*) FROM album_logs l WHERE l.user_id = u.id AND l.deleted_at IS NULL
                                      AND l.removed_at IS NULL)) / ln(51))::numeric(5,4),
    'verified_proof', least(1, (SELECT count(*) FROM submissions s
                                 WHERE s.user_id = u.id AND s.status = 'verified' AND s.removed_at IS NULL) / 5.0)::numeric(5,4),
    'trivia', least(1, (SELECT count(*) FROM trivia_questions q WHERE q.user_id = u.id AND q.correct) / 10.0)::numeric(5,4),
    'prompts', least(1, (SELECT count(*) FROM prompt_answers a WHERE a.user_id = u.id AND a.deleted_at IS NULL
                                AND a.removed_at IS NULL) / 10.0)::numeric(5,4))
  FROM users u WHERE u.id = p_user;
$$;

-- The equal-weight average of a parts object.
CREATE FUNCTION parts_average(p_parts jsonb) RETURNS numeric LANGUAGE sql IMMUTABLE AS $$
  SELECT COALESCE(round(avg(value::numeric), 4), 0) FROM jsonb_each_text(p_parts);
$$;

-- Level 1 to 5 from activity 0..1: below 0.2 is level 1, 0.8 and up is level 5.
CREATE FUNCTION fan_level_for(p_activity numeric) RETURNS smallint LANGUAGE sql IMMUTABLE AS $$
  SELECT least(5, 1 + floor(greatest(0, p_activity) * 5))::smallint;
$$;

-- ---------------------------------------------------------------- stored scores
-- Refreshed hourly by the worker (fans.overall_scores); the draw computes trust fresh instead.
CREATE TABLE fan_overall_scores (
  user_id        uuid PRIMARY KEY REFERENCES users ON DELETE CASCADE,
  trust          numeric(5,4) NOT NULL CHECK (trust BETWEEN 0 AND 1),
  trust_parts    jsonb NOT NULL,
  activity       numeric(5,4) NOT NULL CHECK (activity BETWEEN 0 AND 1),
  activity_parts jsonb NOT NULL,
  level          smallint NOT NULL CONSTRAINT fan_overall_scores_level_check CHECK (level BETWEEN 1 AND 5),
  computed_at    timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT fan_overall_scores_level_matches CHECK (level = fan_level_for(activity))
);
CREATE INDEX fan_overall_scores_level_idx ON fan_overall_scores (level);

-- Recomputes every live fan. Returns how many rows were written. A deleted account's row goes.
CREATE FUNCTION refresh_fan_overall_scores() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE v_count integer;
BEGIN
  DELETE FROM fan_overall_scores f USING users u WHERE u.id = f.user_id AND u.deleted_at IS NOT NULL;
  INSERT INTO fan_overall_scores (user_id, trust, trust_parts, activity, activity_parts, level, computed_at)
  SELECT p.id, parts_average(p.trust_parts), p.trust_parts, parts_average(p.activity_parts), p.activity_parts,
         fan_level_for(parts_average(p.activity_parts)), now()
    FROM (SELECT u.id, fan_trust_parts(u.id) AS trust_parts, fan_activity_parts(u.id) AS activity_parts
            FROM users u WHERE u.deleted_at IS NULL) p
  ON CONFLICT (user_id) DO UPDATE SET
    trust = EXCLUDED.trust, trust_parts = EXCLUDED.trust_parts, activity = EXCLUDED.activity,
    activity_parts = EXCLUDED.activity_parts, level = EXCLUDED.level, computed_at = EXCLUDED.computed_at;
  GET DIAGNOSTICS v_count = ROW_COUNT;
  RETURN v_count;
END $$;

-- ---------------------------------------------------------------- the draw's tie-break
-- Each live registration's trust when the draw ran, written once by run_allocation_selection() and never edited, so
-- the ranking stays repeatable from stored data (with the seed and the registration snapshots).
CREATE TABLE allocation_trust_snapshots (
  allocation_id   uuid NOT NULL REFERENCES allocations ON DELETE RESTRICT,
  registration_id uuid PRIMARY KEY REFERENCES allocation_registrations ON DELETE RESTRICT,
  trust           numeric(5,4) NOT NULL CHECK (trust BETWEEN 0 AND 1)
);
CREATE INDEX allocation_trust_snapshots_allocation_idx ON allocation_trust_snapshots (allocation_id);

CREATE FUNCTION trg_allocation_trust_snapshots_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' AND current_setting('fandom.selecting', true) IS NOT DISTINCT FROM NEW.allocation_id::text THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'selection: trust snapshots are written once by the draw and never edited' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_allocation_trust_snapshots_guard BEFORE INSERT OR UPDATE OR DELETE ON allocation_trust_snapshots
  FOR EACH ROW EXECUTE FUNCTION trg_allocation_trust_snapshots_guard();

-- Same as 0017, with trust between the raw score and the seed: it only orders fans whose snapshot scores are equal.
-- Before the draw (no snapshots) every trust counts as 0, so the order is the 0017 one.
CREATE OR REPLACE FUNCTION allocation_ranking(p_allocation uuid, p_seed text)
RETURNS TABLE (registration_id uuid, rank integer, plus_one boolean) LANGUAGE sql STABLE AS $$
  SELECT r.id, (row_number() OVER w)::integer, r.plus_one
    FROM allocation_registrations r
    LEFT JOIN allocation_trust_snapshots t ON t.registration_id = r.id
   WHERE r.allocation_id = p_allocation AND r.withdrawn_at IS NULL
  WINDOW w AS (ORDER BY (r.hub_match IS NULL), r.raw_score DESC, COALESCE(t.trust, 0) DESC, md5(p_seed || r.id::text))
   ORDER BY 2;
$$;

-- Same as 0017, plus writing the trust snapshots (computed fresh) before ranking.
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
