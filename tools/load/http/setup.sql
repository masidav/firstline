-- HTTP load-test data (tools/load/README.md, "Full HTTP path"): one active test artist with one show and one
-- published allocation that opens a few seconds from now, and :fans synthetic fans, each with a score for the artist,
-- a hub city, and a signed-in session. Used by run.sh on a throwaway database, and by the beta procedure (README).
--
-- psql variables: run (a short tag, letters and digits: names this run's rows), fans, tickets, city (the show's
-- city; two thirds of the fans have it as their primary hub, the rest have other_city), other_city.
--
-- IDs are derived from the run tag so k6 can work out every fan's user and session id without a file:
--   user    md5('fandom-load:<run>:user:<n>')::uuid      session md5('fandom-load:<run>:session:<n>')::uuid
-- The sessions are only rows here: a token for one is valid only if signed with the API's Auth:SigningKey, which
-- the k6 script reads from a file (a throwaway key locally; on beta, mounted inside the cluster, never copied out).
\set ON_ERROR_STOP 1
BEGIN;

CREATE TEMP TABLE load_ids AS
SELECT md5('fandom-load:' || :'run' || ':artist')::uuid AS artist_id,
       md5('fandom-load:' || :'run' || ':event')::uuid AS event_id,
       md5('fandom-load:' || :'run' || ':allocation')::uuid AS allocation_id;

INSERT INTO artists (id, name, spotify_artist_id, status, added_via, catalog_loaded_at)
SELECT artist_id, 'Load Test ' || :'run' || ' (ignore)', left('loadtest' || :'run' || '0000000000000000000000', 22),
       'onboarding', 'fan', now()
  FROM load_ids;
INSERT INTO artist_baselines (artist_id, dist_params, curve) SELECT artist_id, '{}', '{}' FROM load_ids;
UPDATE artists SET status = 'active' WHERE id = (SELECT artist_id FROM load_ids);

INSERT INTO events (id, artist_id, name, starts_at, city_id)
SELECT event_id, artist_id, 'Load test show ' || :'run', now() + interval '30 days', :'city' FROM load_ids;
INSERT INTO allocations (id, event_id, tickets, face_value_cents, min_raw_score, registration_opens_at,
                         registration_closes_at, published_at)
SELECT allocation_id, event_id, :tickets, 4500, 0, now() + interval '3 seconds', now() + interval '2 days', now()
  FROM load_ids;

INSERT INTO users (id, handle, email)
SELECT md5('fandom-load:' || :'run' || ':user:' || g)::uuid, 'lt' || :'run' || '_' || g,
       'lt' || :'run' || '_' || g || '@example.test'
  FROM generate_series(1, :fans) g;
INSERT INTO user_hub_cities (user_id, position, city_id)
SELECT md5('fandom-load:' || :'run' || ':user:' || g)::uuid, 1, CASE WHEN g % 3 = 0 THEN :'other_city' ELSE :'city' END
  FROM generate_series(1, :fans) g;
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
SELECT md5('fandom-load:' || :'run' || ':user:' || g)::uuid, (SELECT artist_id FROM load_ids), (g % 997) / 10.0, g % 100,
       false, 0.2, '{}', now()
  FROM generate_series(1, :fans) g;
INSERT INTO auth_sessions (id, user_id, expires_at)
SELECT md5('fandom-load:' || :'run' || ':session:' || g)::uuid, md5('fandom-load:' || :'run' || ':user:' || g)::uuid,
       now() + interval '1 day'
  FROM generate_series(1, :fans) g;

COMMIT;
SELECT event_id, allocation_id FROM load_ids \gset
\echo event=:event_id allocation=:allocation_id
