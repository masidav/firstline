-- Per request, the standing reads only the asking fan's own place and score (AllocationEndpoints.Registrations.cs).
SELECT CASE WHEN r.id IS NOT NULL THEN r.hub_match IS NOT NULL ELSE h.position IS NOT NULL END,
       COALESCE(r.raw_score, s.raw_score), COALESCE(r.plus_one, false)
  FROM allocations a JOIN events ev ON ev.id = a.event_id
  LEFT JOIN allocation_registrations r ON r.allocation_id = a.id AND r.user_id = current_setting('load.fan')::uuid AND r.withdrawn_at IS NULL
  LEFT JOIN user_artist_scores s ON s.artist_id = ev.artist_id AND s.user_id = current_setting('load.fan')::uuid
  LEFT JOIN user_hub_cities h ON h.user_id = current_setting('load.fan')::uuid AND h.city_id = ev.city_id
 WHERE a.id = current_setting('load.allocation')::uuid;
