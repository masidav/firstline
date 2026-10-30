-- Automatic flags stay hidden from the fan until a person reviews them (decided 2026-10-30).
--
-- A flag is shown to the fan (shown_to_fan_at) when staff raise it, or when staff uphold a rule flag. Until then the
-- fan gets no notice, doesn't see it at /me/fraud-review or in their data download, and can't appeal it. Registering
-- is still blocked, with a neutral reason (unavailable) instead of under_review. A rule flag staff clear without
-- upholding is never shown. An open rule flag doesn't hold the fan out of the draw (0043); staff hear when a rule
-- flags a fan who is registered for a draw still to run (staff_flag_before_draw), so they can review it in time.

ALTER TABLE fraud_flags ADD COLUMN shown_to_fan_at timestamptz;
UPDATE fraud_flags SET shown_to_fan_at = raised_at WHERE source = 'staff';
UPDATE fraud_flags SET shown_to_fan_at = decided_at WHERE source = 'rule' AND status <> 'open'
   AND EXISTS (SELECT 1 FROM eligibility_audit x WHERE x.flag_id = fraud_flags.id AND x.action = 'flag_upheld');

-- Set once: on insert for staff flags, when a rule flag is first upheld. Never cleared.
CREATE FUNCTION trg_fraud_flags_shown() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    NEW.shown_to_fan_at := CASE WHEN NEW.source = 'staff' THEN NEW.raised_at END;
  ELSIF OLD.shown_to_fan_at IS NOT NULL THEN
    NEW.shown_to_fan_at := OLD.shown_to_fan_at;
  ELSIF NEW.status = 'upheld' AND OLD.status = 'open' THEN
    NEW.shown_to_fan_at := clock_timestamp();
  ELSE
    NEW.shown_to_fan_at := NULL;
  END IF;
  RETURN NEW;
END $$;
-- Runs after trg_fraud_flags_guard (triggers fire in name order), which fixes raised_at.
CREATE TRIGGER trg_fraud_flags_shown BEFORE INSERT OR UPDATE ON fraud_flags
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_flags_shown();

-- Same as 0042, except the fan hears about a flag only once it's shown, and staff hear when a rule flags a fan who is
-- registered for a draw still to run.
CREATE OR REPLACE FUNCTION trg_fraud_flags_audit() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_next record;
BEGIN
  IF TG_OP = 'INSERT' THEN
    INSERT INTO eligibility_audit (user_id, action, reason, flag_id, actor_id, inputs)
    VALUES (NEW.user_id, 'flag_raised', NEW.reason, NEW.id, NEW.raised_by, jsonb_build_object('source', NEW.source));
    IF NEW.shown_to_fan_at IS NOT NULL THEN
      PERFORM notify(NEW.user_id, 'fraud_hold', NEW.id, '{}');
    END IF;
    IF NEW.source = 'rule' THEN
      SELECT e.id AS event_id, e.name AS event_name, a.registration_closes_at INTO v_next
        FROM allocation_registrations r JOIN allocations a ON a.id = r.allocation_id JOIN events e ON e.id = r.event_id
       WHERE r.user_id = NEW.user_id AND r.withdrawn_at IS NULL AND r.outcome IS NULL
       ORDER BY a.registration_closes_at LIMIT 1;
      IF FOUND THEN
        PERFORM notify_staff('staff_flag_before_draw', NEW.id,
          (SELECT jsonb_build_object('handle', u.handle::text, 'flag_id', NEW.id, 'event_id', v_next.event_id,
                                     'event_name', v_next.event_name, 'closes_at', v_next.registration_closes_at)
             FROM users u WHERE u.id = NEW.user_id));
      END IF;
    END IF;
  ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
    INSERT INTO eligibility_audit (user_id, action, reason, flag_id, actor_id, inputs)
    VALUES (NEW.user_id, 'flag_' || NEW.status, NEW.reason, NEW.id, NEW.decided_by,
            jsonb_strip_nulls(jsonb_build_object('from', OLD.status, 'note', NEW.decision_note)));
    -- A rule flag upheld: now the fan is told, as if staff had raised it.
    IF NEW.status = 'upheld' AND OLD.shown_to_fan_at IS NULL AND NEW.shown_to_fan_at IS NOT NULL THEN
      PERFORM notify(NEW.user_id, 'fraud_hold', NEW.id, '{}');
    END IF;
    -- Cleared: told only if they knew; an appeal granted already told them (appeal_decided).
    IF NEW.status = 'cleared' AND NEW.shown_to_fan_at IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM fraud_appeals WHERE flag_id = NEW.id AND status = 'granted') THEN
      PERFORM notify(NEW.user_id, 'fraud_cleared', NEW.id, '{}');
    END IF;
  END IF;
  RETURN NULL;
END $$;

-- Only a flag the fan has been shown can be appealed.
CREATE FUNCTION trg_fraud_appeals_shown() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM fraud_flags WHERE id = NEW.flag_id AND shown_to_fan_at IS NOT NULL) THEN
    RAISE EXCEPTION 'appeal blocked: not_shown' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_fraud_appeals_shown BEFORE INSERT ON fraud_appeals
  FOR EACH ROW EXECUTE FUNCTION trg_fraud_appeals_shown();

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check CHECK (kind IN (
  'draw_selected', 'draw_not_selected', 'seat_backfilled', 'ticket_delivered', 'presale', 'guest_named',
  'fraud_hold', 'fraud_cleared', 'appeal_decided',
  'staff_report', 'staff_review', 'staff_draw', 'staff_redaction', 'staff_show_changed', 'staff_appeal',
  'staff_flag_before_draw'));

-- Same as 0042, except a hold the fan hasn't been shown answers unavailable (neutral) instead of under_review.
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
  IF fan_on_hold(p_user) THEN
    RETURN CASE WHEN EXISTS (SELECT 1 FROM fraud_flags WHERE user_id = p_user AND status IN ('open', 'upheld')
                                AND shown_to_fan_at IS NOT NULL)
                THEN 'under_review' ELSE 'unavailable' END;
  END IF;
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

-- Same as 0042, with the holding flag recorded for unavailable refusals too.
CREATE OR REPLACE FUNCTION log_registration_blocked(p_user uuid, p_allocation uuid, p_reason text) RETURNS boolean
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
          CASE WHEN p_reason IN ('under_review', 'unavailable') THEN fan_hold_flag(p_user) END);
  RETURN true;
END $$;
