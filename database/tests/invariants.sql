-- Database invariant tests. Run against a fresh database with all migrations applied
-- (tests/FandomScore.Tests runs this automatically), or by hand:
--   psql -v ON_ERROR_STOP=1 $(printf -- '-f %s ' database/migrations/*.sql) -f database/tests/invariants.sql
-- Any failed ASSERT aborts with a message; success prints "ALL INVARIANT TESTS PASSED".
-- Uses synthetic data only.

\set ON_ERROR_STOP 1
SET client_min_messages = warning;

-- Runs sql and asserts it raises an error whose message contains expected.
CREATE FUNCTION pg_temp.expect_error(sql text, expected text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE sql;
  RAISE EXCEPTION 'expected error "%" but statement succeeded: %', expected, sql;
EXCEPTION WHEN OTHERS THEN
  IF SQLERRM NOT LIKE '%' || expected || '%' THEN
    RAISE EXCEPTION 'expected error "%" but got "%"', expected, SQLERRM;
  END IF;
END $$;

CREATE FUNCTION pg_temp.active_points(p_source uuid) RETURNS integer LANGUAGE sql AS $$
  SELECT count(*)::int FROM score_events WHERE source_id = p_source AND reversed_at IS NULL;
$$;

INSERT INTO users (id, handle, email) VALUES
  ('00000000-0000-0000-0000-000000000001', 'alice', 'alice@example.test'),
  ('00000000-0000-0000-0000-000000000002', 'bob',   'bob@example.test');

-- ------------------------------------------------------------ artists
DO $$
DECLARE v_created boolean; v_id uuid;
BEGIN
  SELECT o_artist_id, o_created INTO v_id, v_created
    FROM track_artist('00000000-0000-0000-0000-000000000001', 'sp-active', 'Active Artist');
  ASSERT v_created, 'new artist should be created';
  SELECT o_created INTO v_created
    FROM track_artist('00000000-0000-0000-0000-000000000002', 'sp-active', 'Renamed');
  ASSERT NOT v_created, 'existing artist must not be duplicated';
  ASSERT (SELECT name FROM artists WHERE id = v_id) = 'Active Artist', 'name must not change';
END $$;

-- Adding a new artist queues its onboarding in the same transaction (0007); an existing one does not.
DO $$ BEGIN
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'artist.onboard' AND status = 'queued') = 1,
         'track_artist queues one onboarding job';
  ASSERT (SELECT payload->>'artist_id' FROM jobs WHERE kind = 'artist.onboard')
         = (SELECT id::text FROM artists WHERE spotify_artist_id = 'sp-active'), 'onboarding job names the artist';
  ASSERT NOT request_artist_onboarding('00000000-0000-0000-0000-000000000000'), 'unknown artist is not queued';
  ASSERT request_artist_onboarding((SELECT id FROM artists WHERE spotify_artist_id = 'sp-active')),
         'an onboarding artist can be queued again';
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'artist.onboard' AND status = 'queued') = 1,
         'at most one queued onboarding job per artist';
END $$;

SELECT pg_temp.expect_error(
  $$UPDATE artists SET status = 'active' WHERE spotify_artist_id = 'sp-active'$$,
  'needs a baseline before activation');
INSERT INTO artist_baselines (artist_id, dist_params, curve)
  SELECT id, '{}', '[]' FROM artists WHERE spotify_artist_id = 'sp-active';
SELECT pg_temp.expect_error(
  $$UPDATE artists SET status = 'active' WHERE spotify_artist_id = 'sp-active'$$,
  'needs its catalog before activation');
UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE spotify_artist_id = 'sp-active';
DO $$ BEGIN
  ASSERT NOT request_artist_onboarding((SELECT id FROM artists WHERE spotify_artist_id = 'sp-active')),
         'an active artist is not queued for onboarding';
  UPDATE artists SET status = 'paused' WHERE spotify_artist_id = 'sp-active';
  ASSERT NOT request_artist_onboarding((SELECT id FROM artists WHERE spotify_artist_id = 'sp-active')),
         'a paused artist is not queued for onboarding';
  UPDATE artists SET status = 'active' WHERE spotify_artist_id = 'sp-active';
END $$;
DO $$ BEGIN
  ASSERT (SELECT activated_at IS NOT NULL FROM artists WHERE spotify_artist_id = 'sp-active'),
         'activation must stamp activated_at';
END $$;

SELECT track_artist('00000000-0000-0000-0000-000000000002', 'sp-onboarding', 'Still Onboarding', 1);
SELECT pg_temp.expect_error(
  $$SELECT track_artist('00000000-0000-0000-0000-000000000002', 'sp-third', 'Third', 1)$$,
  'daily artist add limit reached');

INSERT INTO albums (id, artist_id, title)
  SELECT '10000000-0000-0000-0000-000000000001', id, 'Active LP' FROM artists WHERE spotify_artist_id = 'sp-active';
-- Catalog columns (0007): types and precisions are constrained; tracks start on disc 1.
SELECT pg_temp.expect_error(
  $$UPDATE albums SET album_type = 'ep' WHERE id = '10000000-0000-0000-0000-000000000001'$$, 'albums_album_type');
SELECT pg_temp.expect_error(
  $$UPDATE albums SET release_date_precision = 'decade' WHERE id = '10000000-0000-0000-0000-000000000001'$$,
  'albums_release_date_precision');
SELECT pg_temp.expect_error(
  $$INSERT INTO tracks (album_id, title, disc_number) VALUES ('10000000-0000-0000-0000-000000000001', 'x', 0)$$,
  'tracks_disc_number_check');

-- Idempotency keys (0009): one per user and key, a real hash, and a response only once finished.
INSERT INTO idempotency_keys (user_id, key, request_sha256)
  VALUES ('00000000-0000-0000-0000-000000000001', 'k1', sha256('body'));
SELECT pg_temp.expect_error(
  $$INSERT INTO idempotency_keys (user_id, key, request_sha256)
      VALUES ('00000000-0000-0000-0000-000000000001', 'k1', sha256('other'))$$, 'idempotency_keys_pkey');
SELECT pg_temp.expect_error(
  $$INSERT INTO idempotency_keys (user_id, key, request_sha256)
      VALUES ('00000000-0000-0000-0000-000000000001', '', sha256('x'))$$, 'idempotency_keys_key_check');
SELECT pg_temp.expect_error(
  $$INSERT INTO idempotency_keys (user_id, key, request_sha256)
      VALUES ('00000000-0000-0000-0000-000000000001', 'k2', '\x00')$$, 'idempotency_keys_request_sha256_check');
SELECT pg_temp.expect_error(
  $$UPDATE idempotency_keys SET status_code = 201 WHERE key = 'k1'$$, 'idempotency_keys_check');
UPDATE idempotency_keys SET status_code = 201, response = '{}' WHERE key = 'k1';
INSERT INTO albums (id, artist_id, title)
  SELECT '10000000-0000-0000-0000-000000000002', id, 'Onboarding LP' FROM artists WHERE spotify_artist_id = 'sp-onboarding';

-- ------------------------------------------------------------ album logs
-- Insert with a long review awards album_logged + review_written.
INSERT INTO album_logs (id, user_id, album_id, review) VALUES
  ('20000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001',
   '10000000-0000-0000-0000-000000000001', repeat('x', 120));
DO $$ BEGIN
  ASSERT pg_temp.active_points('20000000-0000-0000-0000-000000000001') = 2, 'log + review awards';
END $$;

-- Soft delete reverses every award for the log.
UPDATE album_logs SET deleted_at = now() WHERE id = '20000000-0000-0000-0000-000000000001';
DO $$ BEGIN
  ASSERT pg_temp.active_points('20000000-0000-0000-0000-000000000001') = 0, 'soft delete reverses awards';
END $$;

-- A review added by a later edit is awarded; shortening it reverses; restoring re-awards once.
INSERT INTO album_logs (id, user_id, album_id) VALUES
  ('20000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000002',
   '10000000-0000-0000-0000-000000000001');
UPDATE album_logs SET review = repeat('y', 150) WHERE id = '20000000-0000-0000-0000-000000000002';
DO $$ BEGIN
  ASSERT (SELECT count(*) FROM score_events WHERE source_id = '20000000-0000-0000-0000-000000000002'
            AND rule_key = 'review_written' AND reversed_at IS NULL) = 1, 'review via edit awarded';
END $$;
UPDATE album_logs SET review = 'short' WHERE id = '20000000-0000-0000-0000-000000000002';
DO $$ BEGIN
  ASSERT (SELECT count(*) FROM score_events WHERE source_id = '20000000-0000-0000-0000-000000000002'
            AND rule_key = 'review_written' AND reversed_at IS NULL) = 0, 'shortened review reversed';
  ASSERT (SELECT count(*) FROM score_events WHERE source_id = '20000000-0000-0000-0000-000000000002'
            AND rule_key = 'album_logged' AND reversed_at IS NULL) = 1, 'log award kept';
END $$;
UPDATE album_logs SET review = repeat('z', 150) WHERE id = '20000000-0000-0000-0000-000000000002';
UPDATE album_logs SET review = repeat('w', 160) WHERE id = '20000000-0000-0000-0000-000000000002';
DO $$ BEGIN
  ASSERT (SELECT count(*) FROM score_events WHERE source_id = '20000000-0000-0000-0000-000000000002'
            AND rule_key = 'review_written' AND reversed_at IS NULL) = 1, 'one active review award';
END $$;

-- Onboarding artists earn nothing.
INSERT INTO album_logs (id, user_id, album_id) VALUES
  ('20000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000002',
   '10000000-0000-0000-0000-000000000002');
DO $$ BEGIN
  ASSERT pg_temp.active_points('20000000-0000-0000-0000-000000000003') = 0, 'onboarding artist earns nothing';
END $$;

-- ------------------------------------------------------------ comments
-- Daily cap: 7 qualifying comments, 5 awarded.
INSERT INTO comments (album_log_id, user_id, body)
  SELECT '20000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', repeat('c', 25)
    FROM generate_series(1, 7);
DO $$ BEGIN
  ASSERT (SELECT count(*) FROM score_events WHERE rule_key = 'comment_posted') = 5, 'comment cap is 5';
END $$;

-- A reply must be on the same log as its parent.
INSERT INTO comments (id, album_log_id, user_id, body) VALUES
  ('40000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000002',
   '00000000-0000-0000-0000-000000000002', 'parent');
INSERT INTO comments (album_log_id, user_id, body, parent_id) VALUES
  ('20000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 'same-log reply',
   '40000000-0000-0000-0000-000000000001');
SELECT pg_temp.expect_error(
  $$INSERT INTO comments (album_log_id, user_id, body, parent_id) VALUES
      ('20000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', 'cross-log',
       '40000000-0000-0000-0000-000000000001')$$,
  'violates foreign key constraint');

-- ------------------------------------------------------------ submissions
INSERT INTO submissions (id, user_id, artist_id, kind)
  SELECT '30000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', id, 'ticket'
    FROM artists WHERE spotify_artist_id = 'sp-active';
UPDATE submissions SET status = 'verified' WHERE id = '30000000-0000-0000-0000-000000000001';
DO $$ BEGIN
  ASSERT pg_temp.active_points('30000000-0000-0000-0000-000000000001') = 1, 'verify awards';
END $$;
UPDATE submissions SET status = 'rejected' WHERE id = '30000000-0000-0000-0000-000000000001';
DO $$ BEGIN
  ASSERT pg_temp.active_points('30000000-0000-0000-0000-000000000001') = 0, 'reject reverses';
END $$;
UPDATE submissions SET status = 'verified' WHERE id = '30000000-0000-0000-0000-000000000001';
DO $$ BEGIN
  ASSERT pg_temp.active_points('30000000-0000-0000-0000-000000000001') = 1, 're-verify awards again';
  ASSERT (SELECT count(*) FROM score_events WHERE source_id = '30000000-0000-0000-0000-000000000001') = 2,
         'history keeps the reversed row';
END $$;

SELECT pg_temp.expect_error(
  $$INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes, tier)
      VALUES ('30000000-0000-0000-0000-000000000001', 'k1', '\x00', 'image/png', 1, 'archive')$$,
  'submission_files_check');

-- ------------------------------------------------------------ ledger is append-only
SELECT pg_temp.expect_error($$UPDATE score_events SET points = 999$$, 'append-only');
SELECT pg_temp.expect_error($$UPDATE score_events SET reversed_at = NULL WHERE reversed_at IS NOT NULL$$,
                            'append-only');
SELECT pg_temp.expect_error($$DELETE FROM score_events$$, 'append-only');
SELECT pg_temp.expect_error($$TRUNCATE score_events$$, 'append-only');
SELECT pg_temp.expect_error(
  $$INSERT INTO score_events (user_id, artist_id, rule_key, points, source_type, source_id)
      SELECT user_id, artist_id, rule_key, points, source_type, source_id
        FROM score_events WHERE reversed_at IS NULL LIMIT 1$$,
  'score_events_source_uidx');

-- ------------------------------------------------------------ account deletion
-- Soft delete scrubs personal fields, drops Spotify and library data, keeps content and ledger history.
INSERT INTO users (id, handle, email, display_name, is_staff) VALUES
  ('00000000-0000-0000-0000-000000000003', 'carol', 'carol@example.test', 'Carol', true);
INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes) VALUES
  ('00000000-0000-0000-0000-000000000003', 'sp-carol', '\x00', '{}');
INSERT INTO album_logs (id, user_id, album_id) VALUES
  ('20000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000003',
   '10000000-0000-0000-0000-000000000001');
UPDATE users SET deleted_at = now() WHERE handle = 'carol';
DO $$
DECLARE u users%ROWTYPE;
BEGIN
  SELECT * INTO u FROM users WHERE id = '00000000-0000-0000-0000-000000000003';
  ASSERT u.handle IS NULL AND u.email IS NULL AND u.display_name IS NULL AND NOT u.is_staff,
         'deletion scrubs personal fields';
  ASSERT NOT EXISTS (SELECT 1 FROM spotify_connections WHERE user_id = u.id), 'deletion drops Spotify';
  ASSERT NOT EXISTS (SELECT 1 FROM user_artist_signals WHERE user_id = u.id), 'deletion drops library data';
  ASSERT EXISTS (SELECT 1 FROM album_logs WHERE user_id = u.id), 'content keeps its owner row';
  ASSERT EXISTS (SELECT 1 FROM score_events WHERE user_id = u.id), 'ledger history kept';
END $$;

-- Handle and email are free for a new account.
INSERT INTO users (handle, email) VALUES ('carol', 'carol@example.test');

-- Deletion is one-way and fields stay empty.
SELECT pg_temp.expect_error(
  $$UPDATE users SET deleted_at = NULL WHERE id = '00000000-0000-0000-0000-000000000003'$$,
  'cannot be restored');
SELECT pg_temp.expect_error(
  $$UPDATE users SET email = 'x@example.test' WHERE id = '00000000-0000-0000-0000-000000000003'$$,
  'personal fields stay empty');
-- Live accounts still require handle and email.
SELECT pg_temp.expect_error($$INSERT INTO users (handle) VALUES ('noemail')$$, 'users_check');

-- Deleting a user still cascades through the ledger.
DELETE FROM users WHERE handle = 'bob';
DO $$ BEGIN
  ASSERT NOT EXISTS (SELECT 1 FROM score_events WHERE user_id = '00000000-0000-0000-0000-000000000002'),
         'user delete cascades to ledger';
END $$;

-- ------------------------------------------------------------ auth (0002)
SELECT pg_temp.expect_error($$INSERT INTO users (handle, email) VALUES ('no spaces', 'x@example.test')$$,
                            'users_handle_format');
SELECT pg_temp.expect_error($$UPDATE users SET display_name = '' WHERE handle = 'alice'$$,
                            'users_display_name_length');

INSERT INTO users (id, handle, email) VALUES ('00000000-0000-0000-0000-000000000004', 'dave', 'dave@example.test');
INSERT INTO auth_identities (provider, subject, user_id) VALUES
  ('google', 'g-dave', '00000000-0000-0000-0000-000000000004');
INSERT INTO auth_sessions (id, user_id, expires_at) VALUES
  ('00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000004', now() + interval '1 day');
INSERT INTO refresh_tokens (token_hash, session_id, expires_at) VALUES
  (sha256('t1'), '00000000-0000-0000-0000-0000000000a1', now() + interval '1 day');

-- One usable refresh token per session; used tokens stay used.
SELECT pg_temp.expect_error($$INSERT INTO refresh_tokens (token_hash, session_id, expires_at)
  VALUES (sha256('t2'), '00000000-0000-0000-0000-0000000000a1', now() + interval '1 day')$$,
  'refresh_tokens_current_uidx');
UPDATE refresh_tokens SET used_at = now() WHERE token_hash = sha256('t1');
INSERT INTO refresh_tokens (token_hash, session_id, expires_at) VALUES
  (sha256('t2'), '00000000-0000-0000-0000-0000000000a1', now() + interval '1 day');
SELECT pg_temp.expect_error($$UPDATE refresh_tokens SET used_at = NULL WHERE token_hash = sha256('t1')$$,
                            'already used');
SELECT pg_temp.expect_error($$INSERT INTO refresh_tokens (token_hash, session_id, expires_at)
  VALUES ('\x00', '00000000-0000-0000-0000-0000000000a1', now() + interval '1 day')$$, 'refresh_tokens_token_hash_check');

-- Revocation needs a reason and is permanent.
SELECT pg_temp.expect_error($$UPDATE auth_sessions SET revoked_at = now()
  WHERE id = '00000000-0000-0000-0000-0000000000a1'$$, 'auth_sessions_check');
UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'sign_out'
  WHERE id = '00000000-0000-0000-0000-0000000000a1';
SELECT pg_temp.expect_error($$UPDATE auth_sessions SET revoked_at = NULL, revoked_reason = NULL
  WHERE id = '00000000-0000-0000-0000-0000000000a1'$$, 'revocation is permanent');

-- Revocation reasons are a closed list (0003 adds provider_revoked).
INSERT INTO auth_sessions (id, user_id, expires_at) VALUES
  ('00000000-0000-0000-0000-0000000000a2', '00000000-0000-0000-0000-000000000004', now() + interval '1 day');
UPDATE auth_sessions SET revoked_at = now(), revoked_reason = 'provider_revoked'
  WHERE id = '00000000-0000-0000-0000-0000000000a2';
SELECT pg_temp.expect_error($$INSERT INTO auth_sessions (user_id, expires_at, revoked_at, revoked_reason)
  VALUES ('00000000-0000-0000-0000-000000000004', now() + interval '1 day', now(), 'bored')$$,
  'auth_sessions_revoked_reason_check');

-- Deletion removes sessions, tokens, and identities, and a deleted user cannot get new ones.
INSERT INTO auth_sessions (user_id, expires_at) VALUES ('00000000-0000-0000-0000-000000000004', now() + interval '1 day');
UPDATE users SET deleted_at = now() WHERE id = '00000000-0000-0000-0000-000000000004';
DO $$ BEGIN
  ASSERT NOT EXISTS (SELECT 1 FROM auth_sessions WHERE user_id = '00000000-0000-0000-0000-000000000004'),
         'deletion ends sessions';
  ASSERT NOT EXISTS (SELECT 1 FROM refresh_tokens), 'deletion drops refresh tokens';
  ASSERT NOT EXISTS (SELECT 1 FROM auth_identities WHERE user_id = '00000000-0000-0000-0000-000000000004'),
         'deletion frees identities';
END $$;
SELECT pg_temp.expect_error($$INSERT INTO auth_sessions (user_id, expires_at)
  VALUES ('00000000-0000-0000-0000-000000000004', now() + interval '1 day')$$, 'is deleted');
SELECT pg_temp.expect_error($$INSERT INTO auth_identities (provider, subject, user_id)
  VALUES ('google', 'g-dave', '00000000-0000-0000-0000-000000000004')$$, 'is deleted');
-- The freed identity can sign up again as a new account.
INSERT INTO users (id, handle, email) VALUES ('00000000-0000-0000-0000-000000000005', 'dave', 'dave@example.test');
INSERT INTO auth_identities (provider, subject, user_id) VALUES
  ('google', 'g-dave', '00000000-0000-0000-0000-000000000005');

-- ------------------------------------------------------------ jobs (0004)
DO $$ DECLARE a bigint; BEGIN
  a := enqueue_job('test.kind', '{"x": 1}', 'k1');
  ASSERT a IS NOT NULL, 'enqueue returns the job id';
  ASSERT enqueue_job('test.kind', '{"x": 2}', 'k1') IS NULL, 'queued jobs dedupe by key';
  UPDATE jobs SET status = 'running', locked_until = now() + interval '1 minute' WHERE id = a;
  ASSERT enqueue_job('test.kind', '{}', 'k1') IS NOT NULL, 'a running job does not block a new queued one';
  ASSERT enqueue_job('test.kind') IS NOT NULL AND enqueue_job('test.kind') IS NOT NULL, 'no key, no dedupe';
END $$;
SELECT pg_temp.expect_error($$INSERT INTO jobs (kind) VALUES ('Bad Kind')$$, 'jobs_kind_check');
SELECT pg_temp.expect_error($$INSERT INTO jobs (kind, status) VALUES ('x', 'running')$$, 'jobs_check');
SELECT pg_temp.expect_error($$INSERT INTO jobs (kind, status) VALUES ('x', 'succeeded')$$, 'jobs_check');

INSERT INTO job_schedules (kind, every) VALUES ('test.recurring', interval '1 hour');
DO $$ BEGIN
  ASSERT fire_due_schedules() = 1, 'a due schedule fires';
  ASSERT fire_due_schedules() = 0, 'and only once per interval';
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'test.recurring') = 1, 'one job enqueued';
  ASSERT (SELECT next_run_at > now() + interval '59 minutes' FROM job_schedules WHERE kind = 'test.recurring'),
         'next run moves forward by the interval';
END $$;

-- ------------------------------------------------------------ deleted-account purge (0004)
INSERT INTO users (id, handle, email) VALUES ('00000000-0000-0000-0000-000000000006', 'erin', 'erin@example.test');
INSERT INTO album_logs (id, user_id, album_id) VALUES
  ('20000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-000000000006', '10000000-0000-0000-0000-000000000001'),
  ('20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001');
INSERT INTO comments (id, album_log_id, user_id, parent_id, body) VALUES
  -- Erin on Alice's log: answered by Alice (tombstone), unanswered (deleted), and a reply to Alice (deleted).
  ('30000000-0000-0000-0000-0000000000e1', '20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000006', NULL,
   'erin comment that alice answers later'),
  ('30000000-0000-0000-0000-0000000000e2', '20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000006', NULL,
   'erin comment that nobody answered'),
  ('30000000-0000-0000-0000-0000000000a1', '20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000001', NULL,
   'alice comment on her own log'),
  -- Alice on Erin's log: goes with the log.
  ('30000000-0000-0000-0000-0000000000a3', '20000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-000000000001', NULL,
   'alice comment on erin log');
INSERT INTO comments (id, album_log_id, user_id, parent_id, body) VALUES
  ('30000000-0000-0000-0000-0000000000a2', '20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000001',
   '30000000-0000-0000-0000-0000000000e1', 'alice reply to erin'),
  ('30000000-0000-0000-0000-0000000000e3', '20000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-000000000006',
   '30000000-0000-0000-0000-0000000000a1', 'erin reply to alice');
INSERT INTO lists (user_id, title) VALUES ('00000000-0000-0000-0000-000000000006', 'Favourites');
INSERT INTO submissions (id, user_id, artist_id, kind)
  SELECT '40000000-0000-0000-0000-0000000000e1', '00000000-0000-0000-0000-000000000006', id, 'ticket'
    FROM artists WHERE spotify_artist_id = 'sp-active';
INSERT INTO outbound_clicks (user_id, artist_id, kind)
  SELECT '00000000-0000-0000-0000-000000000006', id, 'ticket' FROM artists WHERE spotify_artist_id = 'sp-active';
DO $$ BEGIN
  ASSERT EXISTS (SELECT 1 FROM score_events WHERE user_id = '00000000-0000-0000-0000-000000000006' AND reversed_at IS NULL),
         'erin earned points';
END $$;

SELECT pg_temp.expect_error($$SELECT purge_deleted_user('00000000-0000-0000-0000-000000000006')$$, 'is not deleted');
UPDATE users SET deleted_at = now() WHERE id = '00000000-0000-0000-0000-000000000006';
SELECT pg_temp.expect_error($$SELECT purge_deleted_user('00000000-0000-0000-0000-000000000006')$$, 'retention window');
UPDATE users SET deleted_at = now() - interval '31 days' WHERE id = '00000000-0000-0000-0000-000000000006';
INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes) VALUES
  ('40000000-0000-0000-0000-0000000000e1', 'quarantine/erin.jpg', '\x00', 'image/jpeg', 1);
SELECT pg_temp.expect_error($$SELECT purge_deleted_user('00000000-0000-0000-0000-000000000006')$$, 'delete them from storage first');
DELETE FROM submission_files WHERE storage_key = 'quarantine/erin.jpg';

DO $$ DECLARE erin uuid := '00000000-0000-0000-0000-000000000006'; BEGIN
  ASSERT purge_deleted_user(erin), 'purge runs after the retention window';
  ASSERT NOT purge_deleted_user(erin), 'purge is idempotent';
  ASSERT (SELECT content_purged_at IS NOT NULL FROM users WHERE id = erin), 'purge is recorded';
  ASSERT NOT EXISTS (SELECT 1 FROM album_logs WHERE user_id = erin), 'logs deleted';
  ASSERT NOT EXISTS (SELECT 1 FROM comments WHERE id = '30000000-0000-0000-0000-0000000000a3'), 'comments on the logs go too';
  ASSERT (SELECT body = '[deleted]' AND deleted_at IS NOT NULL FROM comments WHERE id = '30000000-0000-0000-0000-0000000000e1'),
         'answered comment becomes a tombstone';
  ASSERT EXISTS (SELECT 1 FROM comments WHERE id = '30000000-0000-0000-0000-0000000000a2'), 'the reply to it stays';
  ASSERT NOT EXISTS (SELECT 1 FROM comments WHERE id IN ('30000000-0000-0000-0000-0000000000e2',
                                                         '30000000-0000-0000-0000-0000000000e3')),
         'unanswered comments and replies deleted';
  ASSERT EXISTS (SELECT 1 FROM comments WHERE id = '30000000-0000-0000-0000-0000000000a1'), 'other users'' comments stay';
  ASSERT NOT EXISTS (SELECT 1 FROM lists WHERE user_id = erin), 'lists deleted';
  ASSERT NOT EXISTS (SELECT 1 FROM submissions WHERE user_id = erin), 'submissions deleted';
  ASSERT NOT EXISTS (SELECT 1 FROM outbound_clicks WHERE user_id = erin), 'clicks unlinked';
  ASSERT EXISTS (SELECT 1 FROM score_events WHERE user_id = erin), 'ledger rows kept';
  ASSERT NOT EXISTS (SELECT 1 FROM score_events WHERE user_id = erin AND reversed_at IS NULL), 'and no longer count';
END $$;

-- ------------------------------------------------------------ artist add limit is 50 (0004)
DO $$ DECLARE n integer; BEGIN
  SELECT count(*) INTO n FROM artists WHERE added_by = '00000000-0000-0000-0000-000000000001'
     AND tracked_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC');
  FOR i IN n + 1 .. 50 LOOP
    PERFORM track_artist('00000000-0000-0000-0000-000000000001', 'sp-bulk-' || i, 'Bulk ' || i);
  END LOOP;
END $$;
SELECT pg_temp.expect_error(
  $$SELECT track_artist('00000000-0000-0000-0000-000000000001', 'sp-bulk-51', 'Bulk 51')$$,
  'daily artist add limit reached');

-- ------------------------------------------------------------ Spotify sync state (0005)
INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes) VALUES
  ('00000000-0000-0000-0000-000000000001', 'sp-alice', '\x00', '{}');
SELECT pg_temp.expect_error(
  $$UPDATE spotify_connections SET sync_state = 'done' WHERE spotify_user_id = 'sp-alice'$$,
  'spotify_connections_sync_state');
SELECT pg_temp.expect_error(
  $$UPDATE spotify_connections SET sync_error = 'Spotify said: invalid token abc' WHERE spotify_user_id = 'sp-alice'$$,
  'spotify_connections_sync_error');
DO $$
BEGIN
  ASSERT (SELECT sync_state = 'idle' AND tracked_artists_found = 0 FROM spotify_connections
           WHERE spotify_user_id = 'sp-alice'), 'new connections start idle';
  UPDATE spotify_connections SET sync_state = 'failed', sync_error = 'spotify_unavailable'
   WHERE spotify_user_id = 'sp-alice';
  ASSERT request_spotify_sync('00000000-0000-0000-0000-000000000001'), 'sync requested';
  ASSERT request_spotify_sync('00000000-0000-0000-0000-000000000001'), 'second request accepted';
  ASSERT (SELECT sync_state = 'queued' AND sync_error IS NULL FROM spotify_connections
           WHERE spotify_user_id = 'sp-alice'), 'request marks the connection queued and clears the error';
  ASSERT (SELECT count(*) = 1 FROM jobs WHERE kind = 'spotify.sync' AND status = 'queued'
             AND payload->>'user_id' = '00000000-0000-0000-0000-000000000001'), 'one queued sync job per user';
  ASSERT NOT request_spotify_sync('00000000-0000-0000-0000-000000000002'), 'no connection, no sync';
  ASSERT NOT EXISTS (SELECT 1 FROM jobs WHERE payload->>'user_id' = '00000000-0000-0000-0000-000000000002'),
         'and no job';
END $$;

-- ------------------------------------------------------------ scoring (0008)
DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        alice uuid := '00000000-0000-0000-0000-000000000001';
        v record;
BEGIN
  -- 100 liked tracks (saturation) and rank 1 in top artists; nothing else. Even split: 2 of 5 signals full.
  INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums, top_artist_rank, recent_plays)
  VALUES (alice, v_artist, 100, 0, 0, 1, 0)
  ON CONFLICT (user_id, artist_id) DO UPDATE SET liked_tracks = 100, playlist_tracks = 0, distinct_albums = 0,
                                                 top_artist_rank = 1, recent_plays = 0;
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.liked_share = 1 AND v.top_share = 1 AND v.playlist_share = 0, 'saturated signals earn full shares';
  ASSERT v.spotify_score = 40, format('even split of 5 signals: expected 40, got %s', v.spotify_score);
  ASSERT v.raw_score = v.spotify_score + v.ledger_points, 'raw score adds ledger points';

  -- Diminishing returns: 10 liked tracks of 100 earn ln(11)/ln(101), about half.
  UPDATE user_artist_signals SET liked_tracks = 10 WHERE user_id = alice AND artist_id = v_artist;
  ASSERT (SELECT liked_share FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = round(ln(11::numeric) / ln(101::numeric), 4),
         'log curve below saturation';
  UPDATE user_artist_signals SET liked_tracks = 100 WHERE user_id = alice AND artist_id = v_artist;

  -- Changing a weight queues one rescore per active artist and reweights at once.
  DELETE FROM jobs WHERE kind = 'scoring.score_artist';
  UPDATE scoring_rules SET points = 3 WHERE rule_key = 'spotify_liked_track';
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued'
            AND payload->>'artist_id' = v_artist::text) = 1, 'weight change queues a rescore';
  UPDATE scoring_rules SET points = 3 WHERE rule_key = 'spotify_liked_track';
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued'
            AND payload->>'artist_id' = v_artist::text) = 1, 'at most one queued rescore per artist';
  ASSERT (SELECT spotify_score FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = round(100 * 4 / 7.0, 2),
         'weights are relative shares';

  -- A disabled rule drops out of the weights and reports no share.
  UPDATE scoring_rules SET points = 1 WHERE rule_key = 'spotify_liked_track';
  UPDATE scoring_rules SET active = false WHERE rule_key = 'spotify_playlist_track';
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.playlist_share IS NULL AND v.spotify_score = 50, format('disabled rule: expected 50, got %s', v.spotify_score);
  UPDATE scoring_rules SET active = true WHERE rule_key = 'spotify_playlist_track';
END $$;

-- Spotify rules must be known signals with a saturation and a non-negative weight; ledger rules have none.
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = 0 WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_saturation_check');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET points = -1 WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_spotify_weight');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = NULL WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_spotify_weight');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = 5 WHERE rule_key = 'comment_posted'$$, 'scoring_rules_ledger_saturation');
SELECT pg_temp.expect_error($$INSERT INTO scoring_rules (rule_key, points, saturation) VALUES ('spotify_follow', 5, 10)$$, 'scoring_rules_spotify_known');

DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        alice uuid := '00000000-0000-0000-0000-000000000001';
        v_depth uuid;
        v record;
BEGIN
  -- Rescore requests: one queued per artist; full rescore counts active artists only.
  DELETE FROM jobs WHERE kind = 'scoring.score_artist';
  ASSERT request_artist_rescore(v_artist) IS NOT NULL, 'first request queues a job';
  ASSERT request_artist_rescore(v_artist) IS NULL, 'second request is folded into the queued one';
  ASSERT request_full_rescore() = (SELECT count(*) FROM artists WHERE status = 'active'), 'full rescore counts active artists';
  ASSERT NOT EXISTS (SELECT 1 FROM jobs j JOIN artists a ON a.id = (j.payload->>'artist_id')::uuid
                      WHERE j.kind = 'scoring.score_artist' AND a.status <> 'active'), 'only active artists are queued';

  -- Changes that don't move scores queue nothing.
  DELETE FROM jobs WHERE kind = 'scoring.score_artist';
  UPDATE scoring_rules SET description = 'x' WHERE rule_key = 'spotify_liked_track';
  UPDATE scoring_rules SET points = 2 WHERE rule_key = 'comment_posted';
  UPDATE scoring_rules SET points = points WHERE rule_key = 'spotify_liked_track';
  UPDATE scoring_rules SET points = 9 WHERE rule_key = 'no_such_rule';
  ASSERT NOT EXISTS (SELECT 1 FROM jobs WHERE kind = 'scoring.score_artist'), 'no rescore for changes that keep scores';
  UPDATE scoring_rules SET points = 1 WHERE rule_key = 'comment_posted';

  -- Deleting and re-adding a Spotify rule both queue rescores; a deleted rule drops out like a disabled one.
  DELETE FROM scoring_rules WHERE rule_key = 'spotify_recent_play';
  ASSERT EXISTS (SELECT 1 FROM jobs WHERE kind = 'scoring.score_artist'), 'deleting a Spotify rule queues a rescore';
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.recent_share IS NULL AND v.spotify_score = 50, format('4 rules left: expected 50, got %s', v.spotify_score);
  DELETE FROM jobs WHERE kind = 'scoring.score_artist';
  INSERT INTO scoring_rules (rule_key, points, saturation) VALUES ('spotify_recent_play', 1, 50);
  ASSERT EXISTS (SELECT 1 FROM jobs WHERE kind = 'scoring.score_artist'), 'adding a Spotify rule queues a rescore';

  -- All Spotify rules disabled: a Spotify score of 0, not a division by zero.
  UPDATE scoring_rules SET active = false WHERE rule_key LIKE 'spotify\_%';
  ASSERT (SELECT spotify_score FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = 0, 'no weights, no score';
  UPDATE scoring_rules SET active = true WHERE rule_key LIKE 'spotify\_%';

  -- Bad counts don't break the view: negatives count as 0 and a rank below 1 is ignored.
  UPDATE user_artist_signals SET liked_tracks = -5, recent_plays = -1, top_artist_rank = 0
   WHERE user_id = alice AND artist_id = v_artist;
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.liked_share = 0 AND v.recent_share = 0 AND v.top_share = 0, 'bad counts earn nothing';
  -- Top rank: rank 1 full, rank = saturation 1/saturation, past it 0.
  UPDATE user_artist_signals SET liked_tracks = 0, recent_plays = 0, top_artist_rank = 50 WHERE user_id = alice AND artist_id = v_artist;
  ASSERT (SELECT top_share FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = 0.02, 'rank 50 of 50';
  UPDATE user_artist_signals SET top_artist_rank = 80 WHERE user_id = alice AND artist_id = v_artist;
  ASSERT (SELECT top_share FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = 0, 'rank past saturation';

  -- Catalog depth (own artist, so the catalog is known): an empty catalog falls back to the saturation (10); a
  -- smaller catalog is the denominator; delisted releases don't count.
  INSERT INTO artists (name, spotify_artist_id) VALUES ('Depth Artist', 'sp-depth') RETURNING id INTO v_depth;
  INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES (v_depth, '{}', '[]');
  UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE id = v_depth;
  INSERT INTO user_artist_signals (user_id, artist_id, distinct_albums) VALUES (alice, v_depth, 2);
  ASSERT (SELECT depth_share FROM v_raw_scores WHERE user_id = alice AND artist_id = v_depth) = 0.2,
         'empty catalog: 2 of saturation 10';
  INSERT INTO albums (artist_id, title, spotify_album_id) VALUES
    (v_depth, 'One', 'sp-depth-1'), (v_depth, 'Two', 'sp-depth-2'), (v_depth, 'Three', 'sp-depth-3'),
    (v_depth, 'Gone', 'sp-depth-4');
  UPDATE albums SET delisted_at = now() WHERE spotify_album_id = 'sp-depth-4';
  ASSERT (SELECT depth_share FROM v_raw_scores WHERE user_id = alice AND artist_id = v_depth) = round(2 / 3.0, 4),
         '2 of a 3-release catalog (the delisted one does not count)';

  -- Deleted accounts drop out of the view even while their ledger rows wait for the purge.
  ASSERT NOT EXISTS (SELECT 1 FROM v_raw_scores rs JOIN users u ON u.id = rs.user_id WHERE u.deleted_at IS NOT NULL),
         'deleted accounts are not scored';
END $$;

-- An artist leaving active queues the run that removes its scores.
DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
BEGIN
  DELETE FROM jobs WHERE kind = 'scoring.score_artist';
  UPDATE artists SET status = 'paused' WHERE id = v_artist;
  ASSERT EXISTS (SELECT 1 FROM jobs WHERE kind = 'scoring.score_artist' AND payload->>'artist_id' = v_artist::text),
         'pausing queues a rescore';
  ASSERT NOT EXISTS (SELECT 1 FROM v_raw_scores WHERE artist_id = v_artist), 'paused artists are not scored';
  UPDATE artists SET status = 'active' WHERE id = v_artist;
END $$;

-- ------------------------------------------------------------ hub cities (0010)
INSERT INTO users (id, handle, email) VALUES ('00000000-0000-0000-0000-0000000000a1', 'hubfan', 'hubfan@example.test');

SELECT pg_temp.expect_error($$SELECT set_hub_cities('00000000-0000-0000-0000-0000000000a1', '{}')$$, 'choose 1 to 3');
SELECT pg_temp.expect_error(
  $$SELECT set_hub_cities('00000000-0000-0000-0000-0000000000a1', '{us-il-chicago,ca-on-toronto,us-ny-new-york,us-tx-austin}')$$,
  'choose 1 to 3');
SELECT pg_temp.expect_error(
  $$SELECT set_hub_cities('00000000-0000-0000-0000-0000000000a1', '{us-il-chicago,us-il-chicago}')$$, 'listed twice');
SELECT pg_temp.expect_error($$SELECT set_hub_cities('00000000-0000-0000-0000-0000000000a1', '{us-xx-atlantis}')$$, 'unknown city');
SELECT pg_temp.expect_error($$INSERT INTO user_hub_cities VALUES ('00000000-0000-0000-0000-0000000000a1', 4, 'us-il-chicago')$$,
  'user_hub_cities_position_check');
SELECT pg_temp.expect_error($$INSERT INTO cities (id, name, region, country) VALUES ('Chicago', 'Chicago', 'IL', 'US')$$,
  'cities_id_check');

DO $$
DECLARE fan uuid := '00000000-0000-0000-0000-0000000000a1'; v_next timestamptz;
BEGIN
  ASSERT (SELECT count(*) FROM cities WHERE country = 'US') >= 80 AND (SELECT count(*) FROM cities WHERE country = 'CA') >= 15,
         'starter list covers US and Canadian metros';

  -- The first choice is free and doesn't start the weekly clock.
  ASSERT set_hub_cities(fan, '{us-il-chicago,ca-on-toronto}') IS NULL, 'first choice: change any time';
  ASSERT (SELECT array_agg(city_id ORDER BY position) FROM user_hub_cities WHERE user_id = fan) = '{us-il-chicago,ca-on-toronto}',
         'stored in order, primary first';
  ASSERT (SELECT hubs_changed_at FROM users WHERE id = fan) IS NULL, 'first choice is not a change';

  -- A real change starts the clock; the same list again is not a change; a second change within a week is refused.
  v_next := set_hub_cities(fan, '{ca-on-toronto,us-il-chicago,us-ny-new-york}');
  ASSERT v_next BETWEEN now() + interval '7 days' - interval '1 minute' AND now() + interval '7 days', 'next change in a week';
  ASSERT set_hub_cities(fan, '{ca-on-toronto,us-il-chicago,us-ny-new-york}') = v_next, 'same list is not a change';
  ASSERT (SELECT array_agg(position ORDER BY position) FROM user_hub_cities WHERE user_id = fan) = '{1,2,3}', 'no gaps';
  BEGIN
    PERFORM set_hub_cities(fan, '{us-il-chicago}');
    RAISE EXCEPTION 'second change within a week should fail';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE 'hub change limit%', SQLERRM;
  END;
  -- A week later it's allowed again.
  UPDATE users SET hubs_changed_at = now() - interval '8 days' WHERE id = fan;
  ASSERT set_hub_cities(fan, '{us-il-chicago}') IS NOT NULL, 'allowed after a week';

  -- Retired cities can't be newly chosen.
  UPDATE cities SET active = false WHERE id = 'us-tx-austin';
  UPDATE users SET hubs_changed_at = NULL WHERE id = fan;
  BEGIN
    PERFORM set_hub_cities(fan, '{us-tx-austin}');
    RAISE EXCEPTION 'retired city should be refused';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE 'hub cities: unknown city%', SQLERRM;
  END;
  UPDATE cities SET active = true WHERE id = 'us-tx-austin';

  -- Account deletion removes them (location data).
  UPDATE users SET deleted_at = now() WHERE id = fan;
  ASSERT NOT EXISTS (SELECT 1 FROM user_hub_cities WHERE user_id = fan), 'deleted accounts lose their hub cities';
END $$;

-- Migration 0011: top_artists_added counts what a sync added; never negative.
DO $$
DECLARE fan uuid;
BEGIN
  INSERT INTO users (handle, email) VALUES ('inv_top_artists', 'inv_top_artists@example.test') RETURNING id INTO fan;
  INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes)
  VALUES (fan, 'sp-inv-top', '\x00', '{}');
  ASSERT (SELECT top_artists_added FROM spotify_connections WHERE user_id = fan) = 0, 'defaults to 0';
  BEGIN
    UPDATE spotify_connections SET top_artists_added = -1 WHERE user_id = fan;
    RAISE EXCEPTION 'negative top_artists_added should fail';
  EXCEPTION WHEN check_violation THEN NULL;
  END;
  DELETE FROM users WHERE id = fan;
END $$;

-- ------------------------------------------------------------ events and allocations (0011)
-- Fixture: an active artist with a show in Chicago two weeks out.
INSERT INTO artists (id, name, spotify_artist_id) VALUES ('40000000-0000-0000-0000-000000000001', 'Alloc Artist', 'sp-alloc');
INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES ('40000000-0000-0000-0000-000000000001', '{}', '[]');
UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE id = '40000000-0000-0000-0000-000000000001';
INSERT INTO artists (id, name, spotify_artist_id) VALUES ('40000000-0000-0000-0000-000000000002', 'Onboarding Artist', 'sp-alloc-2');
INSERT INTO events (id, artist_id, name, city_id, city, country, starts_at) VALUES
  ('41000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 'Chicago show', 'us-il-chicago',
   'Chicago', 'US', now() + interval '14 days'),
  ('41000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 'No city yet', NULL, NULL, NULL,
   now() + interval '14 days'),
  ('41000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000002', 'Onboarding show', 'us-il-chicago',
   'Chicago', 'US', now() + interval '14 days');

SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 0, 5000, now(), now() + interval '1 day')$$, 'allocations_tickets_check');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, min_percentile, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 10, 5000, 101, now(), now() + interval '1 day')$$, 'allocations_min_percentile_check');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 10, 5000, now() + interval '2 days', now() + interval '1 day')$$,
  'allocations_window_check');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 10, 5000, now(), now() + interval '20 days')$$, 'before the show starts');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES ('41000000-0000-0000-0000-000000000002', 10, 5000, now(), now() + interval '1 day', now())$$, 'needs a city');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES ('41000000-0000-0000-0000-000000000003', 10, 5000, now(), now() + interval '1 day', now())$$, 'artist must be active');
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, cancelled_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 10, 5000, now(), now() + interval '1 day', now())$$, 'allocations_cancel_published');

DO $$
DECLARE upcoming uuid; opened uuid;
BEGIN
  -- Published but not open yet: rules can still change; the show can't move to before registration closes.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 50, 4500, now() + interval '1 day', now() + interval '5 days', now())
  RETURNING id INTO upcoming;
  UPDATE allocations SET tickets = 60, min_percentile = 70 WHERE id = upcoming;
  ASSERT (SELECT tickets FROM allocations WHERE id = upcoming) = 60, 'rules editable before registration opens';
  BEGIN
    UPDATE events SET starts_at = now() + interval '3 days' WHERE id = '41000000-0000-0000-0000-000000000001';
    RAISE EXCEPTION 'moving the show before registration closes should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%before the show starts%', SQLERRM;
  END;

  -- Open: rules and the show's time and city are locked; cancelling still works, once.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 20, 4500, now() - interval '1 hour', now() + interval '5 days', now())
  RETURNING id INTO opened;
  BEGIN
    UPDATE allocations SET tickets = 30 WHERE id = opened;
    RAISE EXCEPTION 'changing an open allocation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%rules are locked%', SQLERRM;
  END;
  BEGIN
    UPDATE events SET city_id = 'ca-on-toronto' WHERE id = '41000000-0000-0000-0000-000000000001';
    RAISE EXCEPTION 'moving an open show should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%locked once an allocation opens%', SQLERRM;
  END;
  BEGIN
    UPDATE allocations SET published_at = NULL WHERE id = opened;
    RAISE EXCEPTION 'unpublishing should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%cannot be undone%', SQLERRM;
  END;
  UPDATE allocations SET cancelled_at = now() WHERE id = opened;
  BEGIN
    UPDATE allocations SET cancelled_at = NULL WHERE id = opened;
    RAISE EXCEPTION 'uncancelling should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%cancelled allocation can''t change%', SQLERRM;
  END;
  -- With the open allocation cancelled, the show's city can change again.
  UPDATE events SET city_id = 'ca-on-toronto' WHERE id = '41000000-0000-0000-0000-000000000001';
END $$;

DO $$
DECLARE show uuid := '41000000-0000-0000-0000-000000000001'; draft uuid; live uuid; other_show uuid;
BEGIN
  -- Registration must close strictly before the show.
  BEGIN
    INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
    SELECT show, 5, 100, starts_at - interval '1 day', starts_at FROM events WHERE id = show;
    RAISE EXCEPTION 'closing as the show starts should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%before the show starts%', SQLERRM;
  END;

  -- Publishing through an update checks the same preconditions as on insert, plus a window that isn't over.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000002', 5, 100, now() + interval '1 day', now() + interval '2 days') RETURNING id INTO draft;
  BEGIN
    UPDATE allocations SET published_at = now() WHERE id = draft;
    RAISE EXCEPTION 'publishing without a city should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%needs a city%', SQLERRM;
  END;
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000003', 5, 100, now() + interval '1 day', now() + interval '2 days') RETURNING id INTO draft;
  BEGIN
    UPDATE allocations SET published_at = now() WHERE id = draft;
    RAISE EXCEPTION 'publishing for an onboarding artist should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%artist must be active%', SQLERRM;
  END;
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES (show, 5, 100, now() - interval '2 days', now() - interval '1 day') RETURNING id INTO draft;
  BEGIN
    UPDATE allocations SET published_at = now() WHERE id = draft;
    RAISE EXCEPTION 'publishing a window that is over should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%already be over%', SQLERRM;
  END;
  -- Drafts can be deleted; published ones can't, and neither can a show that has allocations.
  DELETE FROM allocations WHERE id = draft;

  -- Publishing and cancelling are stamped by the database, whatever time is sent.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES (show, 5, 100, now() + interval '1 day', now() + interval '2 days', now() + interval '100 days') RETURNING id INTO live;
  ASSERT (SELECT published_at <= clock_timestamp() FROM allocations WHERE id = live), 'published_at is stamped now';
  BEGIN
    DELETE FROM allocations WHERE id = live;
    RAISE EXCEPTION 'deleting a published allocation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%cancelled, not deleted%', SQLERRM;
  END;
  BEGIN
    DELETE FROM events WHERE id = show;
    RAISE EXCEPTION 'deleting a show with allocations should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%allocations_event_id_fkey%' OR SQLERRM LIKE '%cancelled, not deleted%', SQLERRM;
  END;

  -- An allocation stays with its show; the show keeps its artist and a city while one is published.
  SELECT id INTO other_show FROM events WHERE id = '41000000-0000-0000-0000-000000000003';
  BEGIN
    UPDATE allocations SET event_id = other_show WHERE id = live;
    RAISE EXCEPTION 'moving an allocation to another show should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%belongs to its show%', SQLERRM;
  END;
  BEGIN
    UPDATE events SET artist_id = '40000000-0000-0000-0000-000000000002' WHERE id = show;
    RAISE EXCEPTION 'changing the artist under a published allocation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%artist and city are locked%', SQLERRM;
  END;
  BEGIN
    UPDATE events SET city_id = NULL WHERE id = show;
    RAISE EXCEPTION 'removing the city under a published allocation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%artist and city are locked%', SQLERRM;
  END;

  UPDATE allocations SET cancelled_at = now() + interval '100 days' WHERE id = live;
  ASSERT (SELECT cancelled_at <= clock_timestamp() FROM allocations WHERE id = live), 'cancelled_at is stamped now';
  BEGIN
    UPDATE allocations SET tickets = 6 WHERE id = live;
    RAISE EXCEPTION 'editing a cancelled allocation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%can''t change%', SQLERRM;
  END;

  -- The rules lock uses the clock, not the transaction start: this block's now() is fixed, but after waiting past
  -- the opening time the rules are locked.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES (show, 5, 100, clock_timestamp() + interval '200 milliseconds', now() + interval '2 days', now()) RETURNING id INTO live;
  PERFORM pg_sleep(0.3);
  BEGIN
    UPDATE allocations SET min_percentile = 99 WHERE id = live;
    RAISE EXCEPTION 'changing rules after opening (same transaction) should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%rules are locked%', SQLERRM;
  END;
END $$;

-- ------------------------------------------------------------ registrations (0011)
INSERT INTO users (id, handle, email) VALUES
  ('42000000-0000-0000-0000-000000000001', 'reg_local', 'reg_local@example.test'),
  ('42000000-0000-0000-0000-000000000002', 'reg_low', 'reg_low@example.test'),
  ('42000000-0000-0000-0000-000000000003', 'reg_nohub', 'reg_nohub@example.test'),
  ('42000000-0000-0000-0000-000000000004', 'reg_noscore', 'reg_noscore@example.test'),
  ('42000000-0000-0000-0000-000000000005', 'reg_far', 'reg_far@example.test');
SELECT set_hub_cities('42000000-0000-0000-0000-000000000001', '{us-ny-new-york,us-il-chicago}');
SELECT set_hub_cities('42000000-0000-0000-0000-000000000002', '{us-il-chicago}');
SELECT set_hub_cities('42000000-0000-0000-0000-000000000004', '{us-il-chicago}');
SELECT set_hub_cities('42000000-0000-0000-0000-000000000005', '{ca-on-toronto}');
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight) VALUES
  ('42000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 72.5, 91.2, true, 0.9),
  ('42000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 10, 40, true, 0.9),
  ('42000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001', 90, 99, true, 0.9),
  ('42000000-0000-0000-0000-000000000005', '40000000-0000-0000-0000-000000000001', 60, 75, true, 0.9);
INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
  ('43000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 'Chicago again', 'us-il-chicago',
   now() + interval '20 days');
INSERT INTO allocations (id, event_id, tickets, face_value_cents, min_percentile, registration_opens_at, registration_closes_at, published_at) VALUES
  ('44000000-0000-0000-0000-000000000001', '43000000-0000-0000-0000-000000000001', 10, 4500, 50, now() - interval '1 hour', now() + interval '5 days', now()),
  ('44000000-0000-0000-0000-000000000002', '43000000-0000-0000-0000-000000000001', 5, 9000, 0, now() - interval '1 hour', now() + interval '5 days', now()),
  ('44000000-0000-0000-0000-000000000003', '43000000-0000-0000-0000-000000000001', 5, 9000, 0, now() + interval '1 day', now() + interval '5 days', now()),
  ('44000000-0000-0000-0000-000000000004', '43000000-0000-0000-0000-000000000001', 5, 9000, 0, now() - interval '1 hour', now() + interval '5 days', now());

DO $$
DECLARE fan uuid := '42000000-0000-0000-0000-000000000001'; ga uuid := '44000000-0000-0000-0000-000000000001';
        vip uuid := '44000000-0000-0000-0000-000000000002'; later uuid := '44000000-0000-0000-0000-000000000003';
        r record; v_id uuid; v_far uuid;
BEGIN
  -- Eligibility reasons.
  ASSERT registration_blocker(fan, later) = 'not_open', 'not open yet';
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000002', ga) = 'below_threshold', 'percentile 40 < 50';
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000003', ga) = 'hub_city_required', 'no hub city';
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000004', ga) = 'no_score', 'no score for the artist';
  ASSERT registration_blocker(fan, ga) IS NULL, 'eligible';

  -- Registering keeps a snapshot: hubs (primary first), where the show's city sits in them, and the score.
  v_id := register_for_allocation(fan, ga);
  ASSERT register_for_allocation(fan, ga) = v_id, 'registering again returns the same registration';
  SELECT * INTO r FROM allocation_registrations WHERE id = v_id;
  ASSERT r.hub_city_ids = '{us-ny-new-york,us-il-chicago}' AND r.hub_match = 2, 'hub snapshot, Chicago is the backup';
  ASSERT r.percentile = 91.2 AND r.raw_score = 72.5 AND r.is_estimated AND r.baseline_weight = 0.9
         AND r.score_computed_at IS NOT NULL AND r.event_id = '43000000-0000-0000-0000-000000000001', 'score snapshot';
  -- A fan from elsewhere can register; they're just not local.

  v_far := register_for_allocation('42000000-0000-0000-0000-000000000005', ga);
  ASSERT (SELECT hub_match IS NULL FROM allocation_registrations WHERE id = v_far), 'not local';

  -- A later hub change or score change doesn't touch it.
  UPDATE user_artist_scores SET percentile = 10 WHERE user_id = fan;
  PERFORM set_hub_cities(fan, '{ca-on-toronto}');
  ASSERT (SELECT hub_city_ids = '{us-ny-new-york,us-il-chicago}' AND percentile = 91.2 FROM allocation_registrations WHERE id = v_id),
         'snapshot is frozen';
  UPDATE user_artist_scores SET percentile = 91.2 WHERE user_id = fan;

  -- One live registration per show, across its allocations.
  ASSERT registration_blocker(fan, vip) = 'already_registered', 'one per show';
  BEGIN
    PERFORM register_for_allocation(fan, vip);
    RAISE EXCEPTION 'second registration for the same show should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'registration blocked: already_registered%', SQLERRM;
  END;

  -- The snapshot can't be edited; registrations aren't deleted or truncated.
  BEGIN
    UPDATE allocation_registrations SET percentile = 99 WHERE id = v_id;
    RAISE EXCEPTION 'editing the snapshot should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only withdrawing%', SQLERRM;
  END;
  BEGIN
    UPDATE allocation_registrations SET hub_city_ids = '{us-il-chicago}' WHERE id = v_id;
    RAISE EXCEPTION 'editing the hubs should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only withdrawing%', SQLERRM;
  END;
  BEGIN
    DELETE FROM allocation_registrations WHERE id = v_id;
    RAISE EXCEPTION 'deleting a registration should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%withdrawn, not deleted%', SQLERRM;
  END;
  BEGIN
    TRUNCATE allocation_registrations;
    RAISE EXCEPTION 'truncating registrations should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%withdrawn, not deleted%', SQLERRM;
  END;

  -- Direct inserts can't point at another show, and the database stamps the time.
  BEGIN
    INSERT INTO allocation_registrations (allocation_id, event_id, user_id, hub_city_ids, percentile, raw_score,
                                          is_estimated, baseline_weight, score_computed_at)
    VALUES (vip, '41000000-0000-0000-0000-000000000001', '42000000-0000-0000-0000-000000000002', '{us-il-chicago}',
            99, 99, false, 0, now());
    RAISE EXCEPTION 'a registration for the wrong show should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%allocation_registrations_allocation_id_event_id_fkey%', SQLERRM;
  END;

  -- Withdrawing records who and when (the time is stamped by the database) and frees the show.
  ASSERT withdraw_registration(fan, ga), 'withdrawn';
  ASSERT NOT withdraw_registration(fan, ga), 'nothing left to withdraw';
  ASSERT (SELECT withdrawn_reason = 'fan' AND withdrawn_at <= clock_timestamp() FROM allocation_registrations WHERE id = v_id),
         'fan withdrawal';
  v_id := register_for_allocation(fan, vip);
  ASSERT (SELECT count(*) FROM allocation_registrations WHERE user_id = fan) = 2, 'withdrawn row kept';
  BEGIN
    UPDATE allocation_registrations SET withdrawn_at = NULL, withdrawn_reason = NULL WHERE user_id = fan AND allocation_id = ga;
    RAISE EXCEPTION 'un-withdrawing should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only withdrawing%', SQLERRM;
  END;
  -- Also where no other live registration would trip the unique index first.
  PERFORM withdraw_registration('42000000-0000-0000-0000-000000000005', ga);
  BEGIN
    UPDATE allocation_registrations SET withdrawn_at = NULL, withdrawn_reason = NULL
     WHERE user_id = '42000000-0000-0000-0000-000000000005' AND allocation_id = ga;
    RAISE EXCEPTION 'un-withdrawing should fail on the guard';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only withdrawing%', SQLERRM;
  END;

  -- Cancelling an allocation withdraws its registrations, so those fans can take another allocation of the show.
  UPDATE allocations SET cancelled_at = now() WHERE id = vip;
  ASSERT (SELECT withdrawn_reason = 'allocation_cancelled' FROM allocation_registrations WHERE id = v_id), 'cancel withdraws';
  ASSERT registration_blocker(fan, '44000000-0000-0000-0000-000000000004') IS NULL, 'free to take another allocation';
  PERFORM register_for_allocation(fan, '44000000-0000-0000-0000-000000000004');

  -- A published allocation with registrations can't be deleted.
  BEGIN
    DELETE FROM allocations WHERE id = vip;
    RAISE EXCEPTION 'deleting an allocation with registrations should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%cancelled, not deleted%', SQLERRM;
  END;

  -- Deleting the account withdraws live registrations and empties the hub cities in all of them.
  UPDATE users SET deleted_at = now() WHERE id = fan;
  ASSERT NOT EXISTS (SELECT 1 FROM allocation_registrations WHERE user_id = fan
                       AND (withdrawn_at IS NULL OR hub_city_ids <> '{}' OR hub_match IS NOT NULL
                            OR withdrawn_reason <> 'account_deleted')),
         'account deletion withdraws and scrubs';
  BEGIN
    PERFORM register_for_allocation(fan, '44000000-0000-0000-0000-000000000004');
    RAISE EXCEPTION 'a deleted account registering should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'registration blocked: account_deleted%', SQLERRM;
  END;

  -- A paused artist's allocations take no registrations.
  UPDATE artists SET status = 'paused' WHERE id = '40000000-0000-0000-0000-000000000001';
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000003', '44000000-0000-0000-0000-000000000004') = 'artist_not_active',
         'paused artist';
  UPDATE artists SET status = 'active' WHERE id = '40000000-0000-0000-0000-000000000001';
END $$;

-- Closed registration: no registering or fan withdrawing. (Window moved into the past directly.)
DO $$
DECLARE fan uuid := '42000000-0000-0000-0000-000000000002'; free uuid := '44000000-0000-0000-0000-000000000004';
BEGIN
  PERFORM register_for_allocation(fan, free);
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour'
   WHERE id = free;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000005', free) = 'not_open', 'closed';
  BEGIN
    PERFORM withdraw_registration(fan, free);
    RAISE EXCEPTION 'withdrawing after close should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'registration blocked: not_open%', SQLERRM;
  END;
END $$;

\echo ALL INVARIANT TESTS PASSED
