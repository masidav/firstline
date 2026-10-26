-- What StandingSnapshots loads, at most once per allocation every few seconds per API pod.
SELECT user_id, hub_match IS NOT NULL, raw_score, CASE WHEN plus_one THEN 2 ELSE 1 END
  FROM allocation_registrations WHERE allocation_id = current_setting('load.allocation')::uuid AND withdrawn_at IS NULL;
