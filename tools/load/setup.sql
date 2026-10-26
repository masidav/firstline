-- Load-test data: one active artist with :shows Chicago shows, each with one published allocation open now, and
-- :fans fans (two thirds in Chicago, a third in Toronto) with a score for the artist. Throwaway databases only.
INSERT INTO artists (id, name, spotify_artist_id, status, added_via, catalog_loaded_at)
VALUES ('00000000-0000-0000-0000-0000000000a1', 'Load Test Band', 'loadtestband0000000001', 'onboarding', 'fan', now());
INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES ('00000000-0000-0000-0000-0000000000a1', '{}', '{}');
UPDATE artists SET status = 'active' WHERE id = '00000000-0000-0000-0000-0000000000a1';

CREATE TABLE load_shows (n int PRIMARY KEY, event_id uuid NOT NULL, allocation_id uuid NOT NULL);
INSERT INTO load_shows SELECT g, gen_random_uuid(), gen_random_uuid() FROM generate_series(1, :shows) g;
INSERT INTO events (id, artist_id, name, starts_at, city_id)
SELECT event_id, '00000000-0000-0000-0000-0000000000a1', 'Load show ' || n, now() + interval '30 days', 'us-il-chicago' FROM load_shows;
INSERT INTO allocations (id, event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
SELECT allocation_id, event_id, 2000, 4500, 0, now() + interval '1 second', now() + interval '20 days', now() FROM load_shows;

CREATE TABLE load_fans (n int PRIMARY KEY, id uuid NOT NULL);
INSERT INTO users (handle, email) SELECT 'load_' || g, 'load_' || g || '@example.test' FROM generate_series(1, :fans) g;
INSERT INTO load_fans SELECT substr(handle, 6)::int, id FROM users WHERE handle LIKE 'load\_%';
INSERT INTO user_hub_cities (user_id, position, city_id)
SELECT id, 1, CASE WHEN n % 3 = 0 THEN 'ca-on-toronto' ELSE 'us-il-chicago' END FROM load_fans;
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown, computed_at)
SELECT id, '00000000-0000-0000-0000-0000000000a1', (n % 997) / 10.0, n % 100, false, 0.2, '{}', now() FROM load_fans;
CREATE SEQUENCE load_seq;
