-- Suspensions and allocations (decided 2026-10-24). A suspended account can't get a ticket: it can't register
-- (registration_blocker, 0018) and can't be named as a +1 guest. A host suspended after registering is still ranked
-- in the draw; if selected, their ticket is on hold until staff resolve the suspension (the API reports it as
-- on_hold; ticket delivery must check is_suspended). While suspended, a host can't add a +1 or name a guest.
-- is_suspended() comes from 0018.

-- Covers every path that adds a +1 or names a guest: the API's update while registration is open, add_plus_one(),
-- and name_plus_one_guest(). Dropping a +1 or the draw itself is never blocked.
CREATE FUNCTION trg_registration_suspension_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF ((NEW.plus_one AND NOT OLD.plus_one) OR (NEW.seats = 2 AND OLD.seats = 1)
      OR (NEW.guest_user_id IS NOT NULL AND OLD.guest_user_id IS NULL))
     AND is_suspended(NEW.user_id) THEN
    RAISE EXCEPTION 'plus one blocked: host_suspended' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.guest_user_id IS NOT NULL AND OLD.guest_user_id IS NULL AND is_suspended(NEW.guest_user_id) THEN
    RAISE EXCEPTION 'guest blocked: guest_suspended' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_registration_suspension_guard BEFORE UPDATE ON allocation_registrations
  FOR EACH ROW EXECUTE FUNCTION trg_registration_suspension_guard();
