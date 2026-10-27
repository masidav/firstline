-- The draw runs as a worker job (allocations.select) instead of inside the staff request: a draw is linear in the
-- registrations (about 0.4 ms each), so a very large allocation would outlast a request timeout. Staff get the
-- answer "the draw can't run" at once, from the same checks run_allocation_selection() makes, and the job runs the
-- draw once registration has closed. Nothing depends on how quickly the draw runs: the ranking is fixed by the
-- registration snapshots and the stored seed, not by the time the draw happens.

-- Why the draw can't run for this allocation right now, or null if it can (or already has). Reasons, in the order
-- checked: not_found (or a draft), cancelled, registration_open, artist_not_active. Same rules as
-- run_allocation_selection(), which still enforces them under its lock.
CREATE FUNCTION allocation_selection_blocker(p_allocation uuid) RETURNS text LANGUAGE plpgsql STABLE AS $$
DECLARE al record;
BEGIN
  SELECT a.*, ar.status AS artist_status INTO al
    FROM allocations a JOIN events ev ON ev.id = a.event_id JOIN artists ar ON ar.id = ev.artist_id
   WHERE a.id = p_allocation;
  IF NOT FOUND OR al.published_at IS NULL THEN RETURN 'not_found'; END IF;
  IF al.selected_at IS NOT NULL THEN RETURN NULL; END IF;
  IF al.cancelled_at IS NOT NULL THEN RETURN 'cancelled'; END IF;
  IF clock_timestamp() < al.registration_closes_at THEN RETURN 'registration_open'; END IF;
  IF al.artist_status <> 'active' THEN RETURN 'artist_not_active'; END IF;
  RETURN NULL;
END $$;
