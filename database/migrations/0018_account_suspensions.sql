-- Account suspensions (Phase 9): staff flag an account (for example as a bot) for a set time. While it lasts the fan
-- can't register for allocations or post (photos, sharing, album logs); what they already posted stays, and staff
-- can take it down separately. The fan can ask once per suspension to be unbanned; staff lift it or deny with a reply.

CREATE TABLE account_suspensions (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id    uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  reason     text NOT NULL CHECK (reason IN ('bot', 'other')),
  message    text CHECK (char_length(message) <= 500),     -- shown to the fan
  note       text CHECK (char_length(note) <= 2000),       -- staff only
  created_by uuid REFERENCES users ON DELETE SET NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  ends_at    timestamptz NOT NULL,
  lifted_at  timestamptz,                                   -- ended early by staff
  lifted_by  uuid REFERENCES users ON DELETE SET NULL,
  CHECK (ends_at > created_at),
  CHECK (lifted_at IS NULL OR lifted_at >= created_at)
);
CREATE INDEX account_suspensions_user_idx ON account_suspensions (user_id, ends_at DESC);

-- The suspension in force for a fan, if any (the latest one wins if staff ever overlap them).
CREATE FUNCTION active_suspension(p_user uuid) RETURNS account_suspensions LANGUAGE sql STABLE AS $$
  SELECT * FROM account_suspensions
   WHERE user_id = p_user AND lifted_at IS NULL AND ends_at > clock_timestamp()
   ORDER BY ends_at DESC LIMIT 1;
$$;

CREATE FUNCTION is_suspended(p_user uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM account_suspensions
                  WHERE user_id = p_user AND lifted_at IS NULL AND ends_at > clock_timestamp());
$$;

-- One request to be unbanned per suspension.
CREATE TABLE suspension_appeals (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  suspension_id uuid NOT NULL UNIQUE REFERENCES account_suspensions ON DELETE CASCADE,
  message       text NOT NULL CHECK (char_length(message) BETWEEN 1 AND 1000),
  created_at    timestamptz NOT NULL DEFAULT now(),
  decided_at    timestamptz,
  decision      text CHECK (decision IN ('lifted', 'denied')),
  reply         text CHECK (char_length(reply) <= 500),   -- shown to the fan
  decided_by    uuid REFERENCES users ON DELETE SET NULL,
  CHECK ((decided_at IS NULL) = (decision IS NULL))
);
CREATE INDEX suspension_appeals_open_idx ON suspension_appeals (created_at) WHERE decided_at IS NULL;

-- Staff decisions on accounts join the moderation audit trail.
ALTER TABLE moderation_actions
  DROP CONSTRAINT moderation_actions_target_type_check,
  ADD CONSTRAINT moderation_actions_target_type_check CHECK (target_type IN ('photo', 'log', 'account')),
  DROP CONSTRAINT moderation_actions_action_check,
  ADD CONSTRAINT moderation_actions_action_check
    CHECK (action IN ('take_down', 'dismiss', 'suspend', 'lift_suspension', 'deny_appeal'));

-- registration_blocker() as in 0017, plus: a suspended fan can't register.
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
  IF is_suspended(p_user) THEN RETURN 'suspended'; END IF;
  IF al.artist_status <> 'active' THEN RETURN 'artist_not_active'; END IF;
  IF NOT EXISTS (SELECT 1 FROM user_hub_cities WHERE user_id = p_user) THEN RETURN 'hub_city_required'; END IF;
  SELECT raw_score INTO v_raw FROM user_artist_scores WHERE user_id = p_user AND artist_id = al.artist_id;
  IF NOT FOUND THEN RETURN 'no_score'; END IF;
  IF v_raw < al.min_raw_score THEN RETURN 'below_threshold'; END IF;
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.user_id = p_user
               AND r.withdrawn_at IS NULL AND r.allocation_id <> p_allocation) THEN
    RETURN 'already_registered';
  END IF;
  -- Someone's named guest for this show already has a seat coming; they can't also register for it.
  IF EXISTS (SELECT 1 FROM allocation_registrations r WHERE r.event_id = al.event_id AND r.guest_user_id = p_user
               AND r.withdrawn_at IS NULL) THEN
    RETURN 'already_registered';
  END IF;
  RETURN NULL;
END $$;
