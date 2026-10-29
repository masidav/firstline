-- Fraud review, appeals, and an eligibility audit log (Phase 10, 2026-10-29).
--
-- Staff flag a fan they suspect of gaming their score (a streaming farm, a bot, shared devices, a second account).
-- While a flag is open (under review) or upheld (confirmed), the fan is "on hold":
-- - they can't register for an allocation (registration_blocker() answers under_review),
-- - the draw withdraws their live registrations (withdrawn_reason fraud_hold) before ranking, so they get no seat,
-- - a returned seat skips them.
-- A registration made before the flag stays live until the draw, so clearing the flag in time restores it.
-- Tickets already given out stay with the fan: what to do about them is an open product decision (PROJECT_TODO.md).
--
-- The fan is told they're under review and can appeal once per flag, with a message. Staff grant the appeal (the flag
-- is cleared) or deny it (the flag is upheld). Staff see the evidence and their internal note; the fan sees the reason
-- category, the status, and the staff response to their appeal.
--
-- eligibility_audit records every eligibility decision with what it was based on: each registration (with its score
-- snapshot), each blocked registration attempt (once an hour per fan, allocation, and reason), fans held out of a
-- draw or skipped for a returned seat, and every flag and appeal change with the staff member who made it.
-- Append-only, written by triggers and these functions.

-- ---------------------------------------------------------------- flags
CREATE TABLE fraud_flags (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       uuid NOT NULL REFERENCES users ON DELETE RESTRICT,
  reason        text NOT NULL CONSTRAINT fraud_flags_reason_check
                  CHECK (reason IN ('streaming_farm', 'bot', 'shared_device', 'duplicate_account', 'other')),
  -- What the flag is based on, for staff only (play patterns, linked accounts, ...). Detection rules fill it later.
  evidence      jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(evidence) = 'object'),
  note          text CHECK (length(note) <= 2000),   -- staff only
  source        text NOT NULL DEFAULT 'staff' CHECK (source IN ('staff', 'rule')),
  raised_by     uuid REFERENCES users ON DELETE RESTRICT,
  raised_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
  status        text NOT NULL DEFAULT 'open' CONSTRAINT fraud_flags_status_check
                  CHECK (status IN ('open', 'upheld', 'cleared')),
  decided_by    uuid REFERENCES users ON DELETE RESTRICT,
  decided_at    timestamptz,
  decision_note text CHECK (length(decision_note) <= 2000),   -- staff only
  CONSTRAINT fraud_flags_raised_by_check CHECK (source = 'rule' OR raised_by IS NOT NULL),
  CONSTRAINT fraud_flags_decided_check CHECK ((status = 'open') = (decided_at IS NULL))
);
-- One open flag per fan at a time: more evidence goes on the open one's review.
CREATE UNIQUE INDEX fraud_flags_one_open_idx ON fraud_flags (user_id) WHERE status = 'open';
CREATE INDEX fraud_flags_user_idx ON fraud_flags (user_id, raised_at DESC);
CREATE INDEX fraud_flags_status_idx ON fraud_flags (status, raised_at);

-- The fan is on hold while any flag is open or upheld.
CREATE FUNCTION fan_on_hold(p_user uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM fraud_flags WHERE user_id = p_user AND status IN ('open', 'upheld'));
$$;

-- The flag that holds the fan (the newest), or null.
CREATE FUNCTION fan_hold_flag(p_user uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
  SELECT id FROM fraud_flags WHERE user_id = p_user AND status IN ('open', 'upheld') ORDER BY raised_at DESC LIMIT 1;
$$;

-- ---------------------------------------------------------------- appeals
CREATE TABLE fraud_appeals (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  flag_id     uuid NOT NULL UNIQUE REFERENCES fraud_flags ON DELETE RESTRICT,   -- one appeal per flag
  user_id     uuid NOT NULL REFERENCES users ON DELETE RESTRICT,
  message     text NOT NULL CHECK (length(btrim(message)) BETWEEN 1 AND 2000),
  filed_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
  status      text NOT NULL DEFAULT 'pending' CONSTRAINT fraud_appeals_status_check
                CHECK (status IN ('pending', 'granted', 'denied')),
  decided_by  uuid REFERENCES users ON DELETE RESTRICT,
  decided_at  timestamptz,
  response    text CHECK (length(response) <= 2000),   -- shown to the fan
  CONSTRAINT fraud_appeals_decided_check CHECK ((status = 'pending') = (decided_at IS NULL)),
  CONSTRAINT fraud_appeals_decider_check CHECK (status = 'pending' OR decided_by IS NOT NULL)
);
CREATE INDEX fraud_appeals_pending_idx ON fraud_appeals (filed_at) WHERE status = 'pending';

-- ---------------------------------------------------------------- the audit log
CREATE TABLE eligibility_audit (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  at              timestamptz NOT NULL DEFAULT clock_timestamp(),
  user_id         uuid NOT NULL REFERENCES users ON DELETE RESTRICT,
  action          text NOT NULL CONSTRAINT eligibility_audit_action_check CHECK (action IN (
                    'registered', 'registration_blocked', 'draw_held_out', 'backfill_skipped',
                    'flag_raised', 'flag_upheld', 'flag_cleared', 'appeal_filed', 'appeal_granted', 'appeal_denied')),
  -- A registration_blocked reason (registration_blocker()), or a flag's reason.
  reason          text,
  allocation_id   uuid REFERENCES allocations ON DELETE RESTRICT,
  registration_id uuid REFERENCES allocation_registrations ON DELETE RESTRICT,
  flag_id         uuid REFERENCES fraud_flags ON DELETE RESTRICT,
  appeal_id       uuid REFERENCES fraud_appeals ON DELETE RESTRICT,
  -- Who decided: the fan (registering, appealing) or a staff member; null for the system (the draw, a rule).
  actor_id        uuid REFERENCES users ON DELETE RESTRICT,
  -- What the decision was based on: the score snapshot and threshold, the hub match, the flag.
  inputs          jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(inputs) = 'object'),
  CONSTRAINT eligibility_audit_subject_check CHECK (CASE
    WHEN action IN ('registered', 'draw_held_out', 'backfill_skipped') THEN allocation_id IS NOT NULL AND registration_id IS NOT NULL
    WHEN action = 'registration_blocked' THEN allocation_id IS NOT NULL AND reason IS NOT NULL
    WHEN action LIKE 'flag_%' THEN flag_id IS NOT NULL
    ELSE flag_id IS NOT NULL AND appeal_id IS NOT NULL END)
);
CREATE INDEX eligibility_audit_user_idx ON eligibility_audit (user_id, id DESC);
CREATE INDEX eligibility_audit_allocation_idx ON eligibility_audit (allocation_id, id DESC) WHERE allocation_id IS NOT NULL;
CREATE INDEX eligibility_audit_blocked_idx ON eligibility_audit (user_id, allocation_id, reason, at)
  WHERE action = 'registration_blocked';

CREATE FUNCTION trg_eligibility_audit_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'eligibility audit: rows are never changed or deleted' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_eligibility_audit_guard BEFORE UPDATE OR DELETE ON eligibility_audit
  FOR EACH ROW EXECUTE FUNCTION trg_eligibility_audit_guard();
CREATE TRIGGER trg_eligibility_audit_no_truncate BEFORE TRUNCATE ON eligibility_audit
  FOR EACH STATEMENT EXECUTE FUNCTION trg_eligibility_audit_guard();

-- What a registration decision is based on.
CREATE FUNCTION registration_audit_inputs(p_registration uuid) RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object('raw_score', r.raw_score, 'percentile', r.percentile, 'is_estimated', r.is_estimated,
                            'score_computed_at', r.score_computed_at, 'hub_match', r.hub_match,
                            'min_raw_score', a.min_raw_score)
    FROM allocation_registrations r JOIN allocations a ON a.id = r.allocation_id
   WHERE r.id = p_registration;
$$;

-- Every registration is logged with its snapshot.
CREATE FUNCTION trg_registration_audit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO eligibility_audit (user_id, action, allocation_id, registration_id, actor_id, inputs)
  VALUES (NEW.user_id, 'registered', NEW.allocation_id, NEW.id, NEW.user_id, registration_audit_inputs(NEW.id));
  RETURN NULL;
END $$;
CREATE TRIGGER trg_registration_audit AFTER INSERT ON allocation_registrations
  FOR EACH ROW EXECUTE FUNCTION trg_registration_audit();

-- A blocked registration attempt, called by the API after register_for_allocation() refused. Logged at most once an
-- hour per fan, allocation, and reason, so retrying can't flood the log. Returns whether a row was written.
CREATE FUNCTION log_registration_blocked(p_user uuid, p_allocation uuid, p_reason text) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE v_inputs jsonb;
BEGIN
  IF EXISTS (SELECT 1 FROM eligibility_audit
              WHERE action = 'registration_blocked' AND user_id = p_user AND allocation_id = p_allocation
                AND reason = p_reason AND at > clock_timestamp() - interval '1 hour') THEN
    RETURN false;
  END IF;
  SELECT jsonb_build_object('raw_score', s.raw_score, 'percentile', s.percentile, 'is_estimated', s.is_estimated,
                            'score_computed_at', s.computed_at, 'min_raw_score', a.min_raw_score,
                            'hub_match', (SELECT h.position FROM user_hub_cities h WHERE h.user_id = p_user AND h.city_id = ev.city_id),
                            'flag_id', fan_hold_flag(p_user))
    INTO v_inputs
    FROM allocations a JOIN events ev ON ev.id = a.event_id
    LEFT JOIN user_artist_scores s ON s.artist_id = ev.artist_id AND s.user_id = p_user
   WHERE a.id = p_allocation;
  IF NOT FOUND THEN
    RETURN false;
  END IF;
  INSERT INTO eligibility_audit (user_id, action, reason, allocation_id, actor_id, inputs, flag_id)
  VALUES (p_user, 'registration_blocked', p_reason, p_allocation, p_user, jsonb_strip_nulls(v_inputs),
          CASE WHEN p_reason = 'under_review' THEN fan_hold_flag(p_user) END);
  RETURN true;
END $$;

-- ---------------------------------------------------------------- flag and appeal rules
-- A flag's facts never change; its status moves open -> upheld or cleared, or upheld -> cleared (an appeal granted).
-- A cleared flag is final.
CREATE FUNCTION trg_fraud_flags_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fraud flag: flags are never deleted' USING ERRCODE = 'P0001';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'open' OR NEW.decided_at IS NOT NULL OR NEW.decided_by IS NOT NULL OR NEW.decision_note IS NOT NULL THEN
      RAISE EXCEPTION 'fraud flag: a new flag is open' USING ERRCODE = 'P0001';
    END IF;
    NEW.raised_at := clock_timestamp();
    RETURN NEW;
  END IF;
  IF (NEW.id, NEW.user_id, NEW.reason, NEW.evidence, NEW.note, NEW.source, NEW.raised_by, NEW.raised_at)
     IS DISTINCT FROM (OLD.id, OLD.user_id, OLD.reason, OLD.evidence, OLD.note, OLD.source, OLD.raised_by, OLD.raised_at) THEN
    RAISE EXCEPTION 'fraud flag: only the decision changes' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status THEN
    IF NOT ((OLD.status = 'open' AND NEW.status IN ('upheld', 'cleared')) OR (OLD.status = 'upheld' AND NEW.status = 'cleared')) THEN
      RAISE EXCEPTION 'fraud flag: % can''t become %', OLD.status, NEW.status USING ERRCODE = 'P0001';
    END IF;
    IF NEW.decided_by IS NULL THEN
      RAISE EXCEPTION 'fraud flag: a decision records who made it' USING ERRCODE = 'P0001';
    END IF;
    NEW.decided_at := clock_timestamp();
  ELSIF (NEW.decided_by, NEW.decided_at, NEW.decision_note) IS DISTINCT FROM (OLD.decided_by, OLD.decided_at, OLD.decision_note) THEN
    RAISE EXCEPTION 'fraud flag: only the decision changes' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_fraud_flags_guard BEFORE INSERT OR UPDATE OR DELETE ON fraud_flags
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_flags_guard();
CREATE TRIGGER trg_fraud_flags_no_truncate BEFORE TRUNCATE ON fraud_flags
  FOR EACH STATEMENT EXECUTE FUNCTION trg_fraud_flags_guard();

-- An appeal is filed by the flagged fan while the flag holds them; it's decided once.
CREATE FUNCTION trg_fraud_appeals_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE f fraud_flags;
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'fraud appeal: appeals are never deleted' USING ERRCODE = 'P0001';
  END IF;
  IF TG_OP = 'INSERT' THEN
    SELECT * INTO f FROM fraud_flags WHERE id = NEW.flag_id FOR UPDATE;
    IF f.user_id IS DISTINCT FROM NEW.user_id THEN
      RAISE EXCEPTION 'fraud appeal: only the flagged fan appeals' USING ERRCODE = 'P0001';
    END IF;
    IF f.status = 'cleared' THEN
      RAISE EXCEPTION 'appeal blocked: flag_cleared' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.status <> 'pending' OR NEW.decided_at IS NOT NULL OR NEW.decided_by IS NOT NULL OR NEW.response IS NOT NULL THEN
      RAISE EXCEPTION 'fraud appeal: a new appeal is pending' USING ERRCODE = 'P0001';
    END IF;
    NEW.filed_at := clock_timestamp();
    RETURN NEW;
  END IF;
  IF (NEW.id, NEW.flag_id, NEW.user_id, NEW.message, NEW.filed_at)
     IS DISTINCT FROM (OLD.id, OLD.flag_id, OLD.user_id, OLD.message, OLD.filed_at)
     OR OLD.status <> 'pending' THEN
    RAISE EXCEPTION 'fraud appeal: only a pending appeal''s decision changes, once' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.status <> 'pending' THEN
    NEW.decided_at := clock_timestamp();
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_fraud_appeals_guard BEFORE INSERT OR UPDATE OR DELETE ON fraud_appeals
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_appeals_guard();
CREATE TRIGGER trg_fraud_appeals_no_truncate BEFORE TRUNCATE ON fraud_appeals
  FOR EACH STATEMENT EXECUTE FUNCTION trg_fraud_appeals_guard();

-- Deciding an appeal decides the flag in the same transaction: granted clears it, denied upholds an open one.
CREATE FUNCTION decide_fraud_appeal(p_appeal uuid, p_by uuid, p_grant boolean, p_response text) RETURNS fraud_appeals
LANGUAGE plpgsql AS $$
DECLARE ap fraud_appeals; f fraud_flags;
BEGIN
  SELECT * INTO ap FROM fraud_appeals WHERE id = p_appeal FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'appeal blocked: not_found' USING ERRCODE = 'P0001'; END IF;
  IF ap.status <> 'pending' THEN RAISE EXCEPTION 'appeal blocked: already_decided' USING ERRCODE = 'P0001'; END IF;
  SELECT * INTO f FROM fraud_flags WHERE id = ap.flag_id FOR UPDATE;
  UPDATE fraud_appeals SET status = CASE WHEN p_grant THEN 'granted' ELSE 'denied' END, decided_by = p_by,
                           response = NULLIF(btrim(p_response), '')
   WHERE id = p_appeal RETURNING * INTO ap;
  IF p_grant AND f.status <> 'cleared' THEN
    UPDATE fraud_flags SET status = 'cleared', decided_by = p_by, decision_note = 'Appeal granted' WHERE id = f.id;
  ELSIF NOT p_grant AND f.status = 'open' THEN
    UPDATE fraud_flags SET status = 'upheld', decided_by = p_by, decision_note = 'Appeal denied' WHERE id = f.id;
  END IF;
  RETURN ap;
END $$;

-- ---------------------------------------------------------------- logging and notices for flags and appeals
CREATE FUNCTION trg_fraud_flags_audit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    INSERT INTO eligibility_audit (user_id, action, reason, flag_id, actor_id, inputs)
    VALUES (NEW.user_id, 'flag_raised', NEW.reason, NEW.id, NEW.raised_by, jsonb_build_object('source', NEW.source));
    PERFORM notify(NEW.user_id, 'fraud_hold', NEW.id, '{}');
  ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
    INSERT INTO eligibility_audit (user_id, action, reason, flag_id, actor_id, inputs)
    VALUES (NEW.user_id, 'flag_' || NEW.status, NEW.reason, NEW.id, NEW.decided_by,
            jsonb_strip_nulls(jsonb_build_object('from', OLD.status, 'note', NEW.decision_note)));
    -- An appeal granted already told the fan (appeal_decided).
    IF NEW.status = 'cleared' AND NOT EXISTS (SELECT 1 FROM fraud_appeals WHERE flag_id = NEW.id AND status = 'granted') THEN
      PERFORM notify(NEW.user_id, 'fraud_cleared', NEW.id, '{}');
    END IF;
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_fraud_flags_audit AFTER INSERT OR UPDATE ON fraud_flags
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_flags_audit();

CREATE FUNCTION trg_fraud_appeals_audit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    INSERT INTO eligibility_audit (user_id, action, flag_id, appeal_id, actor_id)
    VALUES (NEW.user_id, 'appeal_filed', NEW.flag_id, NEW.id, NEW.user_id);
    PERFORM notify_staff('staff_appeal', NEW.id,
      (SELECT jsonb_build_object('handle', u.handle::text, 'flag_id', NEW.flag_id) FROM users u WHERE u.id = NEW.user_id));
  ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
    INSERT INTO eligibility_audit (user_id, action, flag_id, appeal_id, actor_id)
    VALUES (NEW.user_id, 'appeal_' || NEW.status, NEW.flag_id, NEW.id, NEW.decided_by);
    PERFORM notify(NEW.user_id, 'appeal_decided', NEW.id, jsonb_build_object('status', NEW.status));
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_fraud_appeals_audit AFTER INSERT OR UPDATE ON fraud_appeals
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_appeals_audit();

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered', 'presale', 'guest_named',
  'fraud_hold', 'fraud_cleared', 'appeal_decided',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction', 'staff_show_changed', 'staff_appeal'));

-- ---------------------------------------------------------------- registration, the draw, and backfill
ALTER TABLE allocation_registrations DROP CONSTRAINT allocation_registrations_withdrawn_reason_check;
ALTER TABLE allocation_registrations ADD CONSTRAINT allocation_registrations_withdrawn_reason_check
  CHECK (withdrawn_reason IN ('fan', 'allocation_cancelled', 'account_deleted', 'fraud_hold'));

-- Same as 0041, plus under_review (a fan on hold) right after the allocation and artist checks.
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
  IF fan_on_hold(p_user) THEN RETURN 'under_review'; END IF;
  IF NOT EXISTS (SELECT 1 FROM user_hub_cities WHERE user_id = p_user) THEN RETURN 'hub_city_required'; END IF;
  SELECT raw_score INTO v_raw FROM user_artist_scores WHERE user_id = p_user AND artist_id = al.artist_id;
  IF NOT FOUND THEN RETURN 'no_score'; END IF;
  IF v_raw < al.min_raw_score THEN RETURN 'below_threshold'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.user_id = p_user
               AND r.withdrawn_at IS NULL AND r.allocation_id <> p_allocation) THEN
    RETURN 'already_registered';
  END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.guest_user_id = p_user
               AND r.withdrawn_at IS NULL) THEN
    RETURN 'named_as_guest';
  END IF;
  RETURN NULL;
END $$;

-- Same as 0039, plus: fans on hold are withdrawn (fraud_hold) and logged before the trust snapshots and the ranking.
-- Their flags are locked first so a flag can't be cleared halfway through the draw.
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
             JOIN LATERAL (SELECT * FROM fraud_flags ff WHERE ff.user_id = reg.user_id AND ff.status IN ('open', 'upheld')
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

-- Same as 0020, plus: a fan on hold is skipped (and logged once per registration); the seat goes to the next fan.
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
    SELECT id INTO v_flag FROM fraud_flags WHERE user_id = r.user_id AND status IN ('open', 'upheld')
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
