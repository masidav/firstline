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
  $$INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes, tier, archived_at, redaction_state)
      VALUES ('30000000-0000-0000-0000-000000000001', 'archive/k1', '\x00', 'image/png', 1, 'archive', now(), 'archived')$$,
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
        n_rules numeric := (SELECT count(*) FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%');
        v record;
BEGIN
  -- 100 liked tracks (saturation) and rank 1 in top artists; nothing else. Even split: 2 of n signals full
  -- (n = 18 since migration 0015; the expectations below count the rules so weights can be added later).
  INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums, top_artist_rank, recent_plays)
  VALUES (alice, v_artist, 100, 0, 0, 1, 0)
  ON CONFLICT (user_id, artist_id) DO UPDATE SET liked_tracks = 100, playlist_tracks = 0, distinct_albums = 0,
                                                 top_artist_rank = 1, recent_plays = 0;
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.liked_share = 1 AND v.top_share = 1 AND v.playlist_share = 0, 'saturated signals earn full shares';
  ASSERT v.spotify_score = round(100 * 2 / n_rules, 2), format('even split of %s signals: expected %s, got %s', n_rules, round(100 * 2 / n_rules, 2), v.spotify_score);
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
  ASSERT (SELECT spotify_score FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist) = round(100 * 4 / (n_rules + 2), 2),
         'weights are relative shares';

  -- A disabled rule drops out of the weights and reports no share.
  UPDATE scoring_rules SET points = 1 WHERE rule_key = 'spotify_liked_track';
  UPDATE scoring_rules SET active = false WHERE rule_key = 'spotify_playlist_track';
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.playlist_share IS NULL AND v.spotify_score = round(100 * 2 / (n_rules - 1), 2),
         format('disabled rule: expected %s, got %s', round(100 * 2 / (n_rules - 1), 2), v.spotify_score);
  UPDATE scoring_rules SET active = true WHERE rule_key = 'spotify_playlist_track';
END $$;

-- Spotify rules must be known signals with a saturation and a non-negative weight; ledger rules have none.
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = 0 WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_saturation_check');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET points = -1 WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_spotify_weight');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = NULL WHERE rule_key = 'spotify_liked_track'$$, 'scoring_rules_spotify_weight');
SELECT pg_temp.expect_error($$UPDATE scoring_rules SET saturation = 5 WHERE rule_key = 'comment_posted'$$, 'scoring_rules_ledger_saturation');
SELECT pg_temp.expect_error($$INSERT INTO scoring_rules (rule_key, points, saturation) VALUES ('spotify_bogus', 5, 10)$$, 'scoring_rules_spotify_known');

DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        alice uuid := '00000000-0000-0000-0000-000000000001';
        n_rules numeric := (SELECT count(*) FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%');
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
  ASSERT v.recent_share IS NULL AND v.spotify_score = round(100 * 2 / (n_rules - 1), 2),
         format('one rule fewer: expected %s, got %s', round(100 * 2 / (n_rules - 1), 2), v.spotify_score);
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
SELECT pg_temp.expect_error($$INSERT INTO cities (id, name, region, country, time_zone) VALUES ('Chicago', 'Chicago', 'IL', 'US', 'America/Chicago')$$,
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

-- ------------------------------------------------------------ proof photos and sharing (0012)
SELECT pg_temp.expect_error(
  $$INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes) VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/gif', 10)$$,
  'uploads_purpose_kind_check');   -- one combined check since 0023
SELECT pg_temp.expect_error(
  $$INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key) VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/jpeg', 10, 'k')$$,
  'uploads_check');

DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        alice uuid := '00000000-0000-0000-0000-000000000001';
        v_ticket uuid; v_merch uuid; v_photo uuid;
BEGIN
  INSERT INTO submissions (user_id, artist_id, kind) VALUES (alice, v_artist, 'ticket') RETURNING id INTO v_ticket;
  INSERT INTO submissions (user_id, artist_id, kind) VALUES (alice, v_artist, 'merch') RETURNING id INTO v_merch;
  INSERT INTO submissions (user_id, artist_id, kind, concert_date, concert_venue, concert_city)
  VALUES (alice, v_artist, 'concert_photo', '2026-10-03', 'Metro', 'Chicago') RETURNING id INTO v_photo;

  -- A verified concert photo earns its own rule.
  UPDATE submissions SET status = 'verified' WHERE id = v_photo;
  ASSERT EXISTS (SELECT 1 FROM score_events WHERE source_id = v_photo AND rule_key = 'concert_photo_verified'
                   AND reversed_at IS NULL), 'verified concert photo earns points';
  UPDATE submissions SET shared_at = now() WHERE id = v_photo;

  -- Rejecting it later reverses the points and takes it off the feed.
  UPDATE submissions SET status = 'rejected' WHERE id = v_photo;
  ASSERT (SELECT shared_at IS NULL FROM submissions WHERE id = v_photo), 'rejection unshares';
  ASSERT NOT EXISTS (SELECT 1 FROM score_events WHERE source_id = v_photo AND reversed_at IS NULL), 'and reverses';

  UPDATE submissions SET status = 'verified' WHERE id = v_ticket;
  UPDATE submissions SET status = 'verified' WHERE id = v_merch;
  UPDATE submissions SET shared_at = now() WHERE id = v_merch;
  ASSERT (SELECT shared_at IS NOT NULL FROM submissions WHERE id = v_merch), 'verified merch can be shared';
END $$;

SELECT pg_temp.expect_error(
  $$UPDATE submissions SET shared_at = now() WHERE kind = 'ticket' AND status = 'verified'$$,
  'only merch and concert photos can be shared');
SELECT pg_temp.expect_error(
  $$INSERT INTO submissions (user_id, artist_id, kind, shared_at)
    SELECT '00000000-0000-0000-0000-000000000001', id, 'merch', now() FROM artists WHERE spotify_artist_id = 'sp-active'$$,
  'only verified submissions can be shared');

-- Concert photos are pinned to a concert; a pinned event must be the same artist's show.
SELECT pg_temp.expect_error(
  $$INSERT INTO submissions (user_id, artist_id, kind)
    SELECT '00000000-0000-0000-0000-000000000001', id, 'concert_photo' FROM artists WHERE spotify_artist_id = 'sp-active'$$,
  'submissions_concert_pinned');
SELECT pg_temp.expect_error(
  $$INSERT INTO submissions (user_id, artist_id, kind, concert_date)
    SELECT '00000000-0000-0000-0000-000000000001', id, 'concert_photo', '2026-10-03' FROM artists WHERE spotify_artist_id = 'sp-active'$$,
  'submissions_concert_fields');
DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        v_other uuid;
        v_event uuid;
BEGIN
  INSERT INTO artists (name, spotify_artist_id) VALUES ('Other Band', 'sp-other-band') RETURNING id INTO v_other;
  INSERT INTO events (artist_id, name, venue, city, starts_at) VALUES (v_other, 'Other Band live', 'Metro', 'Chicago', now() - interval '3 days')
    RETURNING id INTO v_event;
  BEGIN
    INSERT INTO submissions (user_id, artist_id, kind, event_id)
    VALUES ('00000000-0000-0000-0000-000000000001', v_artist, 'concert_photo', v_event);
    RAISE EXCEPTION 'pinned another artist''s show';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE '%not a show by this artist%', SQLERRM;
  END;
  INSERT INTO submissions (user_id, artist_id, kind, event_id)
  VALUES ('00000000-0000-0000-0000-000000000001', v_other, 'concert_photo', v_event);
END $$;

-- ------------------------------------------------------------ Spotify signals (0015)
DO $$
DECLARE v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        alice uuid := '00000000-0000-0000-0000-000000000001';
        n_rules numeric := (SELECT count(*) FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%');
        v_album uuid; v_old uuid; v_track uuid; v_old_track uuid; fan uuid; i integer;
        v_releases numeric; v_tracks numeric;
        v record;
BEGIN
  -- Two more releases (the fixture may have some already): one out last week, one years ago, one track each.
  INSERT INTO albums (artist_id, title, spotify_album_id, release_date) VALUES (v_artist, 'New', 'al-inv-new', current_date - 5)
  RETURNING id INTO v_album;
  INSERT INTO albums (artist_id, title, spotify_album_id, release_date) VALUES (v_artist, 'Old', 'al-inv-old', current_date - 2000)
  RETURNING id INTO v_old;
  INSERT INTO tracks (album_id, title, spotify_track_id) VALUES (v_album, 'New 1', 'tr-inv-new') RETURNING id INTO v_track;
  INSERT INTO tracks (album_id, title, spotify_track_id) VALUES (v_old, 'Old 1', 'tr-inv-old') RETURNING id INTO v_old_track;
  SELECT count(DISTINCT al.id), count(t.id) INTO v_releases, v_tracks
    FROM albums al LEFT JOIN tracks t ON t.album_id = al.id WHERE al.artist_id = v_artist AND al.delisted_at IS NULL;
  UPDATE user_artist_signals SET liked_tracks = 0, top_artist_rank = NULL, top_artist_rank_short = 1, top_artist_rank_long = 25,
         top_tracks = 20, playlists_featuring = 5, dedicated_playlists = 1, followed = true
   WHERE user_id = alice AND artist_id = v_artist;
  -- Saved the new album the day it came out (early, and the earliest save: tenure 5 days) and the old one last year.
  INSERT INTO user_album_saves (user_id, album_id, saved_at) VALUES
    (alice, v_album, (current_date - 5)::timestamptz + interval '12 hours'),
    (alice, v_old, now() - interval '365 days')
  ON CONFLICT DO NOTHING;
  -- Plays: 3 this week and 1 in each of the 2 weeks before (streak 3), plus one a year ago (outside 90 days).
  INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at) VALUES
    (alice, v_artist, 'tr-inv-new', now() - interval '1 hour'),
    (alice, v_artist, 'tr-inv-new', now() - interval '2 hours'),
    (alice, v_artist, 'tr-inv-old', date_trunc('week', now()) + interval '1 minute'),
    (alice, v_artist, 'tr-inv-new', date_trunc('week', now()) - interval '3 days'),
    (alice, v_artist, 'tr-inv-new', date_trunc('week', now()) - interval '10 days'),
    (alice, v_artist, 'tr-inv-new', now() - interval '300 days');

  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.top_short_share = 1 AND v.top_long_share = round((50 + 1 - 25) / 50.0, 4), format('short/long ranks: %s %s', v.top_short_share, v.top_long_share);
  ASSERT v.top_track_share = 1, 'top tracks at saturation earn full marks';
  ASSERT v.saved_albums = 2 AND v.saved_album_share = round(2 / least(10, v_releases), 4),
         format('2 saved albums of %s releases: expected %s, got %s', v_releases, round(2 / least(10, v_releases), 4), v.saved_album_share);
  ASSERT v.early_saves = 1 AND v.early_save_share = round(ln(2::numeric) / ln(11::numeric), 4), format('one early save: %s', v.early_save_share);
  ASSERT v.tenure_days = 365 AND v.tenure_share = round(365 / 730.0, 4), format('tenure from the earliest save: %s days', v.tenure_days);
  ASSERT v.playlist_feature_share = 1 AND v.dedicated_playlist_share = 1 AND v.follow_share = 1, 'playlist and follow signals';
  ASSERT v.plays_90d = 5 AND v.play_history_share = round(ln(6::numeric) / ln(101::numeric), 4), format('plays in 90 days: %s', v.plays_90d);
  ASSERT v.streak_weeks = 3 AND v.play_streak_share = round(3 / 12.0, 4), format('streak of 3 weeks, got %s', v.streak_weeks);
  ASSERT v.tracks_touched = 2 AND v.catalog_coverage_share = round(2 / least(50, v_tracks), 4),
         format('2 of %s catalog tracks played: coverage %s, got %s', v_tracks, round(2 / least(50, v_tracks), 4), v.catalog_coverage_share);
  ASSERT v.deep_cuts = 0, 'deep cuts need 10 fans of the artist';

  -- Deep cuts: 10 fans like the new track; only alice has the old one, so it is a deep cut for her.
  FOR i IN 1..9 LOOP
    INSERT INTO users (handle, email) VALUES ('inv_deep_' || i, 'inv_deep_' || i || '@example.test') RETURNING id INTO fan;
    INSERT INTO user_track_likes (user_id, track_id, source) VALUES (fan, v_track, 'liked');
  END LOOP;
  INSERT INTO user_track_likes (user_id, track_id, source) VALUES (alice, v_track, 'liked') ON CONFLICT DO NOTHING;
  SELECT * INTO v FROM v_raw_scores WHERE user_id = alice AND artist_id = v_artist;
  ASSERT v.deep_cuts = 1 AND v.deep_cut_share = round(ln(2::numeric) / ln(11::numeric), 4), format('one deep cut, got %s', v.deep_cuts);
  -- A fan with only plays and no signals row is still scored.
  INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at) VALUES (fan, v_artist, 'tr-inv-old', now());
  ASSERT (SELECT plays_90d FROM v_raw_scores WHERE user_id = fan AND artist_id = v_artist) = 1, 'plays alone score';

  -- The threshold helper: the saved-album signal alone, weighted like the view does it.
  ASSERT album_saves_score(v_artist, 2) = round(100 * least(1, 2 / least(10, v_releases)) / n_rules, 2),
         format('album_saves_score: %s', album_saves_score(v_artist, 2));
  ASSERT album_saves_score(v_artist, 2) = v.saved_album_share * 100 / n_rules
      OR abs(album_saves_score(v_artist, 2) - v.saved_album_share * 100 / n_rules) < 0.01, 'matches the view';
  ASSERT album_saves_score(v_artist, 0) = 0 AND album_saves_score(gen_random_uuid(), 3) = round(100 * 0.3 / n_rules, 2),
         'no catalog falls back to the saturation';
  UPDATE scoring_rules SET active = false WHERE rule_key = 'spotify_saved_album';
  ASSERT album_saves_score(v_artist, 2) = 0, 'disabled rule scores nothing';
  UPDATE scoring_rules SET active = true WHERE rule_key = 'spotify_saved_album';

  -- The ledgers follow deleted users and the artist's albums.
  DELETE FROM users WHERE handle LIKE 'inv_deep_%';
  DELETE FROM user_plays WHERE user_id = alice AND artist_id = v_artist;
  DELETE FROM user_album_saves WHERE user_id = alice;
  DELETE FROM user_track_likes WHERE user_id = alice AND track_id IN (v_track, v_old_track);
  DELETE FROM albums WHERE id IN (v_album, v_old);
  UPDATE user_artist_signals SET liked_tracks = 100, top_artist_rank = 1, top_artist_rank_short = NULL, top_artist_rank_long = NULL,
         top_tracks = 0, playlists_featuring = 0, dedicated_playlists = 0, followed = false
   WHERE user_id = alice AND artist_id = v_artist;
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

-- ------------------------------------------------------------ reports and takedowns (0014)
DO $$
DECLARE alice uuid := '00000000-0000-0000-0000-000000000001';
        bob   uuid;
        v_artist uuid := (SELECT id FROM artists WHERE spotify_artist_id = 'sp-active');
        v_log uuid; v_photo uuid; v_action uuid;
BEGIN
  INSERT INTO users (handle, email) VALUES ('inv_reporter', 'inv_reporter@example.test') RETURNING id INTO bob;
  -- One open report per fan and post.
  INSERT INTO album_logs (user_id, album_id, review)
    SELECT alice, id, repeat('Takedown test review. ', 6) FROM albums WHERE artist_id = v_artist LIMIT 1 RETURNING id INTO v_log;
  INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES (bob, 'log', v_log, 'spam');
  BEGIN
    INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES (bob, 'log', v_log, 'abuse');
    RAISE EXCEPTION 'duplicate open report accepted';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;

  -- Taking a log down reverses its points.
  ASSERT EXISTS (SELECT 1 FROM score_events WHERE source_id = v_log AND reversed_at IS NULL), 'log earned points';
  UPDATE album_logs SET removed_at = now() WHERE id = v_log;
  ASSERT NOT EXISTS (SELECT 1 FROM score_events WHERE source_id = v_log AND reversed_at IS NULL), 'takedown reverses';

  -- Taking a photo down unshares it for good.
  INSERT INTO submissions (user_id, artist_id, kind) VALUES (alice, v_artist, 'merch') RETURNING id INTO v_photo;
  UPDATE submissions SET status = 'verified' WHERE id = v_photo;
  UPDATE submissions SET shared_at = now() WHERE id = v_photo;
  UPDATE submissions SET removed_at = now() WHERE id = v_photo;
  ASSERT (SELECT shared_at IS NULL FROM submissions WHERE id = v_photo), 'takedown unshares';
  BEGIN
    UPDATE submissions SET shared_at = now() WHERE id = v_photo;
    RAISE EXCEPTION 'taken-down photo was shared again';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE '%can''t be shared again%', SQLERRM;
  END;
  ASSERT (SELECT status = 'verified' FROM submissions WHERE id = v_photo), 'proof stays verified';

  -- Staff actions are append-only.
  INSERT INTO moderation_actions (staff_id, target_type, target_id, action, reports) VALUES (bob, 'log', v_log, 'take_down', 1)
    RETURNING id INTO v_action;
  BEGIN
    UPDATE moderation_actions SET action = 'dismiss' WHERE id = v_action;
    RAISE EXCEPTION 'moderation action edited';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE '%append-only%', SQLERRM;
  END;
  BEGIN
    DELETE FROM moderation_actions WHERE id = v_action;
    RAISE EXCEPTION 'moderation action deleted';
  EXCEPTION WHEN OTHERS THEN
    ASSERT SQLERRM LIKE '%append-only%', SQLERRM;
  END;
END $$;
SELECT pg_temp.expect_error(
  $$INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES ('00000000-0000-0000-0000-000000000001', 'user', gen_random_uuid(), 'spam')$$,
  'reports_target_type_check');
SELECT pg_temp.expect_error(
  $$INSERT INTO reports (reporter_id, target_type, target_id, reason, resolved_at) VALUES ('00000000-0000-0000-0000-000000000001', 'log', gen_random_uuid(), 'spam', now())$$,
  'reports_check');

-- ------------------------------------------------------------ events and allocations (0013)
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
SELECT pg_temp.expect_error($$INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at)
  VALUES ('41000000-0000-0000-0000-000000000001', 10, 5000, -1, now(), now() + interval '1 day')$$, 'allocations_min_raw_score_check');
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
  UPDATE allocations SET tickets = 60, min_raw_score = 70 WHERE id = upcoming;
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
    UPDATE allocations SET min_raw_score = 99 WHERE id = live;
    RAISE EXCEPTION 'changing rules after opening (same transaction) should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%rules are locked%', SQLERRM;
  END;
END $$;

-- ------------------------------------------------------------ registrations (0013)
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
INSERT INTO allocations (id, event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at) VALUES
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
  ASSERT registration_blocker('42000000-0000-0000-0000-000000000002', ga) = 'below_threshold', 'raw score 10 < 50';
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
    TRUNCATE allocation_registrations CASCADE;   -- CASCADE: reservations (0020) reference registrations
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

-- ------------------------------------------------------------ +1, threshold, and selection (0017)
INSERT INTO users (id, handle, email) VALUES
  ('45000000-0000-0000-0000-000000000001', 'sel_top', 'sel_top@example.test'),
  ('45000000-0000-0000-0000-000000000002', 'sel_tie_a', 'sel_tie_a@example.test'),
  ('45000000-0000-0000-0000-000000000003', 'sel_tie_b', 'sel_tie_b@example.test'),
  ('45000000-0000-0000-0000-000000000004', 'sel_far', 'sel_far@example.test'),
  ('45000000-0000-0000-0000-000000000005', 'sel_low', 'sel_low@example.test'),
  ('45000000-0000-0000-0000-000000000006', 'sel_guest', 'sel_guest@example.test');
SELECT set_hub_cities(id, CASE WHEN handle = 'sel_far' THEN '{ca-on-toronto}' ELSE '{us-il-chicago}' END::text[])
  FROM users WHERE handle LIKE 'sel\_%';
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight) VALUES
  ('45000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 80, 95, false, 0.1),
  ('45000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 50, 70, false, 0.1),
  ('45000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001', 50, 70, false, 0.1),
  ('45000000-0000-0000-0000-000000000004', '40000000-0000-0000-0000-000000000001', 99, 99, false, 0.1),
  ('45000000-0000-0000-0000-000000000005', '40000000-0000-0000-0000-000000000001', 30, 40, false, 0.1),
  ('45000000-0000-0000-0000-000000000006', '40000000-0000-0000-0000-000000000001', 60, 80, false, 0.1);
INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
  ('46000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 'Draw night', 'us-il-chicago',
   now() + interval '20 days');

DO $$
DECLARE al uuid; low uuid; top uuid := '45000000-0000-0000-0000-000000000001'; tie_a uuid := '45000000-0000-0000-0000-000000000002';
        tie_b uuid := '45000000-0000-0000-0000-000000000003'; far uuid := '45000000-0000-0000-0000-000000000004';
        lowfan uuid := '45000000-0000-0000-0000-000000000005'; guest uuid := '45000000-0000-0000-0000-000000000006';
        s allocation_selections; again allocation_selections; r record;
BEGIN
  -- Publishing without a threshold sets the two-saved-albums default; an explicit threshold is kept.
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES ('46000000-0000-0000-0000-000000000001', 3, 4500, now() - interval '1 hour', now() + interval '5 days', now())
  RETURNING id INTO al;
  ASSERT (SELECT min_raw_score FROM allocations WHERE id = al) = allocation_default_threshold('40000000-0000-0000-0000-000000000001'),
         'default threshold at publish';
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET min_raw_score = 40 WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  ASSERT registration_blocker(lowfan, al) = 'below_threshold', 'raw score 30 < 40';
  BEGIN
    INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, selected_at)
    VALUES ('46000000-0000-0000-0000-000000000001', 3, 4500, now() + interval '1 day', now() + interval '5 days', now());
    RAISE EXCEPTION 'setting selected_at directly should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only the selection draw%', SQLERRM;
  END;

  -- Register: top asks for a +1; tie_a turns one on and off while open.
  PERFORM register_for_allocation(top, al);
  UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = al AND user_id = top;
  PERFORM register_for_allocation(tie_a, al);
  UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = al AND user_id = tie_a;
  UPDATE allocation_registrations SET plus_one = false WHERE allocation_id = al AND user_id = tie_a;
  PERFORM register_for_allocation(tie_b, al);
  PERFORM register_for_allocation(far, al);

  -- Outcomes and seats come only from the draw; the draw waits for registration to close.
  BEGIN
    UPDATE allocation_registrations SET outcome = 'selected', seats = 1 WHERE allocation_id = al AND user_id = far;
    RAISE EXCEPTION 'setting an outcome directly should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only the selection draw%', SQLERRM;
  END;
  BEGIN
    PERFORM run_allocation_selection(al, NULL);
    RAISE EXCEPTION 'selection while open should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'selection blocked: registration_open%', SQLERRM;
  END;

  -- A +1 guest can be named before the draw, once.
  PERFORM name_plus_one_guest(top, al, guest);
  BEGIN
    PERFORM name_plus_one_guest(top, al, lowfan);
    RAISE EXCEPTION 'naming a second guest should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'guest blocked: already_named%', SQLERRM;
  END;
  BEGIN
    PERFORM name_plus_one_guest(tie_b, al, guest);
    RAISE EXCEPTION 'a guest without a +1 should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'guest blocked: no_plus_one%', SQLERRM;
  END;
  ASSERT registration_blocker(guest, al) = 'named_as_guest', 'a named guest can''t also register for the show (until they decline, 0041)';

  -- Close and draw. 3 seats: top (+1) takes 2; tie_a and tie_b tie at 50 for the last one; far (99) isn't local.
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  s := run_allocation_selection(al, NULL);
  ASSERT (s.tickets, s.registrations, s.selected_fans, s.seats_assigned) = (3, 4, 2, 3), format('draw summary %s', s);
  ASSERT (SELECT (outcome, seats, selection_rank) = ('selected', 2::smallint, 1) FROM allocation_registrations
           WHERE allocation_id = al AND user_id = top), 'top first, with the +1';
  ASSERT (SELECT count(*) FROM allocation_registrations WHERE allocation_id = al AND user_id IN (tie_a, tie_b)
           AND outcome = 'selected' AND seats = 1 AND selection_rank = 2) = 1, 'one of the tied fans gets the last seat';
  ASSERT (SELECT (outcome, seats, selection_rank) IS NOT DISTINCT FROM ('not_selected', NULL::smallint, 4) FROM allocation_registrations
           WHERE allocation_id = al AND user_id = far), 'non-local fans after local ones';
  ASSERT (SELECT selected_at = s.ran_at FROM allocations WHERE id = al), 'selected_at stamped';

  -- The draw can be repeated from the seed, runs once, and can't be edited.
  ASSERT NOT EXISTS (SELECT 1 FROM allocation_ranking(al, s.seed) k JOIN allocation_registrations x ON x.id = k.registration_id
                      WHERE x.selection_rank <> k.rank), 'ranking reproduces from the seed';
  again := run_allocation_selection(al, NULL);
  ASSERT again.seed = s.seed AND again.ran_at = s.ran_at, 'second run returns the recorded draw';
  BEGIN
    UPDATE allocation_selections SET seed = 'x' WHERE allocation_id = al;
    RAISE EXCEPTION 'editing a draw should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never edited%', SQLERRM;
  END;
  BEGIN
    DELETE FROM allocation_selections WHERE allocation_id = al;
    RAISE EXCEPTION 'deleting a draw should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never edited%', SQLERRM;
  END;
  BEGIN
    UPDATE allocations SET cancelled_at = now() WHERE id = al;
    RAISE EXCEPTION 'cancelling after the draw should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%fans have been selected%', SQLERRM;
  END;

  -- After the draw: no +1 changes except a selected fan adding one when a seat is free (none is).
  SELECT * INTO r FROM allocation_registrations WHERE allocation_id = al AND outcome = 'selected' AND seats = 1;
  BEGIN
    PERFORM add_plus_one(r.user_id, al);
    RAISE EXCEPTION 'adding a +1 with no seat left should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'plus one blocked: sold_out%', SQLERRM;
  END;
  BEGIN
    PERFORM add_plus_one(far, al);
    RAISE EXCEPTION 'a +1 for a fan who wasn''t selected should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'plus one blocked: not_selected%', SQLERRM;
  END;
  BEGIN
    UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = al AND user_id = far;
    RAISE EXCEPTION 'changing the +1 after closing should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%+1 can change only%', SQLERRM;
  END;
END $$;

-- A +1 that doesn't fit: the fan gets the last seat alone (provisional), and can add the +1 later if a seat frees up.
DO $$
DECLARE al uuid; first uuid := '45000000-0000-0000-0000-000000000004'; second uuid := '45000000-0000-0000-0000-000000000002';
        s allocation_selections;
BEGIN
  INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
    ('46000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 'Small room', 'ca-on-toronto',
     now() + interval '20 days');
  INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
  VALUES ('46000000-0000-0000-0000-000000000002', 3, 4500, 0, now() - interval '1 hour', now() + interval '5 days', now())
  RETURNING id INTO al;
  PERFORM register_for_allocation(first, al);    -- local (Toronto), 99
  PERFORM register_for_allocation(second, al);   -- not local, 50, asks for a +1
  UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = al AND user_id IN (first, second);
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  s := run_allocation_selection(al, NULL);
  ASSERT (SELECT seats FROM allocation_registrations WHERE allocation_id = al AND user_id = first) = 2, 'first takes 2';
  ASSERT (SELECT (outcome, seats, plus_one) = ('selected', 1::smallint, true) FROM allocation_registrations
           WHERE allocation_id = al AND user_id = second), 'last seat without the +1';
  ASSERT s.seats_assigned = 3, 'all seats assigned';
END $$;

-- A selected fan adds a +1 after the draw when a seat is free; then names the guest; the fan's guest can't be themself.
DO $$
DECLARE al uuid; fan uuid := '45000000-0000-0000-0000-000000000003'; guest uuid := '45000000-0000-0000-0000-000000000005';
BEGIN
  INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
    ('46000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001', 'Roomy', 'us-il-chicago',
     now() + interval '20 days');
  INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
  VALUES ('46000000-0000-0000-0000-000000000003', 5, 4500, 0, now() - interval '1 hour', now() + interval '5 days', now())
  RETURNING id INTO al;
  PERFORM register_for_allocation(fan, al);
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  PERFORM run_allocation_selection(al, NULL);
  BEGIN
    PERFORM name_plus_one_guest(fan, al, guest);
    RAISE EXCEPTION 'naming a guest without a +1 seat should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'guest blocked: no_plus_one%', SQLERRM;
  END;
  PERFORM add_plus_one(fan, al);
  PERFORM add_plus_one(fan, al);   -- idempotent
  ASSERT (SELECT (plus_one, seats) = (true, 2::smallint) FROM allocation_registrations WHERE allocation_id = al AND user_id = fan), '+1 added';
  ASSERT (SELECT seats_assigned FROM allocation_selections WHERE allocation_id = al) = 2, 'seat counted once';
  BEGIN
    PERFORM name_plus_one_guest(fan, al, fan);
    RAISE EXCEPTION 'naming yourself should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'guest blocked: self%', SQLERRM;
  END;
  PERFORM name_plus_one_guest(fan, al, guest);
  ASSERT (SELECT guest_user_id = guest AND guest_named_at IS NOT NULL FROM allocation_registrations
           WHERE allocation_id = al AND user_id = fan), 'guest named';
END $$;

-- ------------------------------------------------------------ reservations (0020)
-- A draw of 3 seats among four fans: the top fan with a +1 takes two, the next one. Returning hands the seats on
-- down the ranking; tokens are hidden until delivery; reservations are never made by hand or deleted.
INSERT INTO users (id, handle, email) VALUES
  ('47000000-0000-0000-0000-000000000001', 'res_a', 'res_a@example.test'),
  ('47000000-0000-0000-0000-000000000002', 'res_b', 'res_b@example.test'),
  ('47000000-0000-0000-0000-000000000003', 'res_c', 'res_c@example.test'),
  ('47000000-0000-0000-0000-000000000004', 'res_d', 'res_d@example.test');
SELECT set_hub_cities(id, '{us-il-chicago}'::text[]) FROM users WHERE handle LIKE 'res\_%';
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight) VALUES
  ('47000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001', 90, 99, false, 0.1),
  ('47000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 80, 95, false, 0.1),
  ('47000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001', 70, 90, false, 0.1),
  ('47000000-0000-0000-0000-000000000004', '40000000-0000-0000-0000-000000000001', 60, 85, false, 0.1);
INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
  ('46000000-0000-0000-0000-000000000004', '40000000-0000-0000-0000-000000000001', 'Return night', 'us-il-chicago',
   now() + interval '20 days');
DO $$
DECLARE al uuid; a uuid := '47000000-0000-0000-0000-000000000001'; b uuid := '47000000-0000-0000-0000-000000000002';
        c uuid := '47000000-0000-0000-0000-000000000003'; d uuid := '47000000-0000-0000-0000-000000000004';
        res_a reservations; res_b uuid; res_c uuid;
BEGIN
  INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at,
                           delivery_hours_before_show)
  VALUES ('46000000-0000-0000-0000-000000000004', 3, 4500, 0, now() - interval '1 hour', now() + interval '5 days', now(), 48)
  RETURNING id INTO al;
  PERFORM register_for_allocation(a, al); PERFORM register_for_allocation(b, al);
  PERFORM register_for_allocation(c, al); PERFORM register_for_allocation(d, al);
  UPDATE allocation_registrations SET plus_one = true WHERE allocation_id = al AND user_id = a;
  -- Delivery is a rule: locked once registration has opened.
  BEGIN
    UPDATE allocations SET delivery_hours_before_show = 1 WHERE id = al;
    RAISE EXCEPTION 'changing delivery after opening should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%rules are locked%', SQLERRM;
  END;
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  PERFORM run_allocation_selection(al, NULL);

  -- The draw made a reservation per selected fan, seats and price from the registration and allocation, token hidden
  -- until 48 hours before the show.
  SELECT * INTO res_a FROM reservations WHERE user_id = a;
  ASSERT res_a.seats = 2 AND res_a.face_value_cents = 4500 AND res_a.currency = 'USD', 'reservation copies seats and price';
  ASSERT res_a.token ~ '^[0-9a-f]{64}$', 'token is 64 hex characters';
  ASSERT res_a.deliver_at = (SELECT starts_at - interval '48 hours' FROM events WHERE id = '46000000-0000-0000-0000-000000000004'),
         'delivery time from the allocation rule';
  ASSERT (SELECT count(*) FROM reservations WHERE allocation_id = al) = 2, 'one reservation per selected fan';
  ASSERT NOT EXISTS (SELECT 1 FROM reservations WHERE user_id IN (c, d)), 'not-selected fans hold nothing';
  SELECT id INTO res_b FROM reservations WHERE user_id = b;

  -- Nothing about a reservation changes by hand.
  BEGIN
    INSERT INTO reservations (registration_id, allocation_id, event_id, user_id, seats, face_value_cents, currency, token, deliver_at)
    SELECT r.id, al, r.event_id, c, 1, 4500, 'USD', new_reservation_token(), now() FROM allocation_registrations r WHERE r.user_id = c AND r.allocation_id = al;
    RAISE EXCEPTION 'a hand-made reservation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only a selected registration makes a reservation%', SQLERRM;
  END;
  BEGIN
    UPDATE reservations SET user_id = c WHERE id = res_a.id;
    RAISE EXCEPTION 'moving a reservation to another account should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%holder, show, price, token, and delivery time are fixed%', SQLERRM;
  END;
  BEGIN
    UPDATE reservations SET returned_at = now() WHERE id = res_a.id;
    RAISE EXCEPTION 'returning by hand should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only return_reservation() returns%', SQLERRM;
  END;
  BEGIN
    DELETE FROM reservations WHERE id = res_a.id;
    RAISE EXCEPTION 'deleting a reservation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never deleted%', SQLERRM;
  END;
  BEGIN
    PERFORM return_reservation(b, res_a.id);
    RAISE EXCEPTION 'returning someone else''s reservation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'return blocked: not_found%', SQLERRM;
  END;

  -- A returns both seats: they go to c and d, the next two in the ranking; seats_assigned stays 3.
  PERFORM return_reservation(a, res_a.id);
  PERFORM return_reservation(a, res_a.id);   -- idempotent
  ASSERT (SELECT returned_at IS NOT NULL FROM reservations WHERE id = res_a.id), 'returned';
  ASSERT (SELECT (outcome, seats) = ('selected', 1::smallint) FROM allocation_registrations WHERE allocation_id = al AND user_id = c), 'c backfilled';
  ASSERT (SELECT (outcome, seats) = ('selected', 1::smallint) FROM allocation_registrations WHERE allocation_id = al AND user_id = d), 'd backfilled';
  ASSERT (SELECT count(*) FROM reservations WHERE allocation_id = al AND returned_at IS NULL) = 3, 'b, c, d hold reservations';
  ASSERT (SELECT seats_assigned FROM allocation_selections WHERE allocation_id = al) = 3, 'all seats assigned again';
  ASSERT (SELECT selected_fans FROM allocation_selections WHERE allocation_id = al) = 2, 'the draw''s own count is unchanged';
  SELECT id INTO res_c FROM reservations WHERE user_id = c;
  BEGIN
    PERFORM add_plus_one(a, al);
    RAISE EXCEPTION 'a +1 on a returned reservation should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'plus one blocked: returned%', SQLERRM;
  END;

  -- b deletes their account: the reservation is cancelled and the seat freed (nobody is left to take it).
  UPDATE users SET deleted_at = now() WHERE id = b;
  ASSERT (SELECT (cancelled_at IS NOT NULL, cancelled_reason) = (true, 'account_deleted') FROM reservations WHERE id = res_b), 'cancelled on deletion';
  ASSERT (SELECT seats_assigned FROM allocation_selections WHERE allocation_id = al) = 2, 'seat freed';

  -- Once the show has started nothing can be returned.
  ALTER TABLE events DISABLE TRIGGER trg_event_lock;
  UPDATE events SET starts_at = now() - interval '1 minute' WHERE id = '46000000-0000-0000-0000-000000000004';
  ALTER TABLE events ENABLE TRIGGER trg_event_lock;
  BEGIN
    PERFORM return_reservation(c, res_c);
    RAISE EXCEPTION 'returning after the show started should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'return blocked: show_started%', SQLERRM;
  END;
  ALTER TABLE events DISABLE TRIGGER trg_event_lock;
  UPDATE events SET starts_at = now() + interval '20 days' WHERE id = '46000000-0000-0000-0000-000000000004';
  ALTER TABLE events ENABLE TRIGGER trg_event_lock;
END $$;

-- ------------------------------------------------------------ notifications (0021)
-- The draw tells each fan their outcome once, a backfill tells the fan who got the seat, staff hear about draws,
-- first reports, and proof to review; a delivered ticket is announced once by notify_delivered_tickets().
INSERT INTO users (id, handle, email, is_staff) VALUES
  ('48000000-0000-0000-0000-000000000001', 'note_staff', 'note_staff@example.test', true),
  ('48000000-0000-0000-0000-000000000002', 'note_top', 'note_top@example.test', false),
  ('48000000-0000-0000-0000-000000000003', 'note_next', 'note_next@example.test', false),
  ('48000000-0000-0000-0000-000000000004', 'note_rep', 'note_rep@example.test', false);
SELECT set_hub_cities(id, '{us-il-chicago}'::text[]) FROM users WHERE handle IN ('note_top', 'note_next');
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight) VALUES
  ('48000000-0000-0000-0000-000000000002', '40000000-0000-0000-0000-000000000001', 90, 99, false, 0.1),
  ('48000000-0000-0000-0000-000000000003', '40000000-0000-0000-0000-000000000001', 80, 95, false, 0.1);
INSERT INTO events (id, artist_id, name, city_id, starts_at) VALUES
  ('46000000-0000-0000-0000-000000000005', '40000000-0000-0000-0000-000000000001', 'Notice night', 'us-il-chicago',
   now() + interval '20 days');
DO $$
DECLARE al uuid; staff uuid := '48000000-0000-0000-0000-000000000001'; top uuid := '48000000-0000-0000-0000-000000000002';
        nxt uuid := '48000000-0000-0000-0000-000000000003'; rep uuid := '48000000-0000-0000-0000-000000000004';
        res uuid; sub uuid; log_id uuid; n integer;
BEGIN
  INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
  VALUES ('46000000-0000-0000-0000-000000000005', 1, 4500, 0, now() - interval '1 hour', now() + interval '5 days', now())
  RETURNING id INTO al;
  PERFORM register_for_allocation(top, al);
  PERFORM register_for_allocation(nxt, al);
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  PERFORM run_allocation_selection(al, NULL);

  ASSERT (SELECT kind FROM notifications WHERE user_id = top) = 'draw_selected', 'the selected fan hears so';
  ASSERT (SELECT kind FROM notifications WHERE user_id = nxt) = 'draw_not_selected', 'the other fan hears too';
  ASSERT (SELECT data->>'event_name' FROM notifications WHERE user_id = top) = 'Notice night', 'with the show';
  ASSERT EXISTS (SELECT 1 FROM notifications WHERE user_id = staff AND kind = 'staff_draw' AND subject_id = al
                   AND (data->>'selected_fans')::int = 1), 'staff hear about the draw';
  PERFORM run_allocation_selection(al, NULL);   -- idempotent draw: no second notices
  ASSERT (SELECT count(*) FROM notifications WHERE user_id IN (top, nxt, staff) AND kind LIKE '%draw%') = 3, 'no duplicates';

  -- top returns the seat; next gets it and is told.
  SELECT id INTO res FROM reservations WHERE user_id = top;
  PERFORM return_reservation(top, res);
  ASSERT EXISTS (SELECT 1 FROM notifications WHERE user_id = nxt AND kind = 'seat_backfilled'), 'backfilled fan hears so';

  -- Delivery is announced once, when its time has passed.
  ASSERT notify_delivered_tickets() = 0, 'nothing delivered yet';
  ALTER TABLE reservations DISABLE TRIGGER trg_reservation_guard;
  UPDATE reservations SET deliver_at = now() - interval '1 minute' WHERE user_id = nxt;
  ALTER TABLE reservations ENABLE TRIGGER trg_reservation_guard;
  ASSERT notify_delivered_tickets() = 1, 'one ticket delivered';
  ASSERT notify_delivered_tickets() = 0, 'announced once';
  ASSERT NOT EXISTS (SELECT 1 FROM notifications WHERE user_id = top AND kind = 'ticket_delivered'), 'a returned ticket is not announced';

  -- Staff hear about the first open report on a post, not every one.
  INSERT INTO album_logs (user_id, album_id, visibility) VALUES (top, '10000000-0000-0000-0000-000000000001', 'public') RETURNING id INTO log_id;
  INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES (rep, 'log', log_id, 'spam');
  INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES (nxt, 'log', log_id, 'abuse');
  ASSERT (SELECT count(*) FROM notifications WHERE user_id = staff AND kind = 'staff_report') = 1, 'one notice per reported post';

  -- Proof waiting for review.
  INSERT INTO submissions (user_id, artist_id, kind, status) VALUES (top, '40000000-0000-0000-0000-000000000001', 'merch', 'needs_review')
  RETURNING id INTO sub;
  ASSERT EXISTS (SELECT 1 FROM notifications WHERE user_id = staff AND kind = 'staff_review' AND subject_id = sub), 'staff hear about proof';

  -- Only read_at changes, once.
  UPDATE notifications SET read_at = now() WHERE user_id = staff;
  BEGIN
    UPDATE notifications SET read_at = NULL WHERE user_id = staff;
    RAISE EXCEPTION 'unreading should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only read_at changes, once%', SQLERRM;
  END;
  BEGIN
    UPDATE notifications SET kind = 'staff_draw' WHERE user_id = top;
    RAISE EXCEPTION 'changing a notification should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only read_at changes, once%', SQLERRM;
  END;

  -- A deleted account's notifications go, and it gets no new ones.
  UPDATE users SET deleted_at = now() WHERE id = top;
  ASSERT NOT EXISTS (SELECT 1 FROM notifications WHERE user_id = top), 'deletion removes notifications';
  PERFORM notify(top, 'draw_selected', gen_random_uuid());
  ASSERT NOT EXISTS (SELECT 1 FROM notifications WHERE user_id = top), 'a deleted account gets none';
  SELECT count(*) INTO n FROM notifications WHERE user_id = staff;
  ASSERT n = 3, 'staff: draw, report, review';
END $$;

-- ------------------------------------------------------------ daily trivia (0022)
-- One question a day; answered once, today only; a right answer earns trivia_correct once (0.30, a tenth of a log).
INSERT INTO users (id, handle, email) VALUES ('49000000-0000-0000-0000-000000000001', 'quiz_fan', 'quiz_fan@example.test');
DO $$
DECLARE fan uuid := '49000000-0000-0000-0000-000000000001'; artist uuid := '40000000-0000-0000-0000-000000000001';
        q uuid; old_q uuid; was_right boolean;
BEGIN
  ASSERT (SELECT points FROM scoring_rules WHERE rule_key = 'trivia_correct') = 0.30, 'a tenth of album_logged';
  INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
  VALUES (fan, (now() AT TIME ZONE 'UTC')::date, artist, 'release_year', 'What year?', ARRAY['2019', '2020', '2021'], 1)
  RETURNING id INTO q;
  BEGIN
    INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
    VALUES (fan, (now() AT TIME ZONE 'UTC')::date, artist, 'release_year', 'Again?', ARRAY['a', 'b'], 0);
    RAISE EXCEPTION 'a second question today should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    UPDATE trivia_questions SET chosen_index = 1, correct = true, answered_at = now() WHERE id = q;
    RAISE EXCEPTION 'answering by hand should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%answered once, through answer_trivia()%', SQLERRM;
  END;
  BEGIN
    PERFORM answer_trivia('49000000-0000-0000-0000-0000000000ff', q, 1);
    RAISE EXCEPTION 'someone else answering should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'trivia blocked: not_found%', SQLERRM;
  END;
  was_right := answer_trivia(fan, q, 1);
  ASSERT was_right, 'the right option is right';
  ASSERT (SELECT points FROM score_events WHERE source_type = 'trivia' AND source_id = q AND reversed_at IS NULL) = 0.30, 'points awarded';
  BEGIN
    PERFORM answer_trivia(fan, q, 0);
    RAISE EXCEPTION 'answering twice should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'trivia blocked: answered%', SQLERRM;
  END;
  BEGIN
    UPDATE trivia_questions SET prompt = 'Changed' WHERE id = q;
    RAISE EXCEPTION 'changing a question should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%a question never changes%', SQLERRM;
  END;
  -- An older question stays open until it's answered (0035).
  INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
  VALUES (fan, trivia_week_day() - 14, artist, 'track_album', 'Which album?', ARRAY['A', 'B'], 0)
  RETURNING id INTO old_q;
  ASSERT answer_trivia(fan, old_q, 1) = false, 'a question from two weeks ago can still be answered';
  BEGIN
    INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
    VALUES (fan, (now() AT TIME ZONE 'UTC')::date + 1, artist, 'track_album', 'Out of range', ARRAY['A', 'B'], 2);
    RAISE EXCEPTION 'an answer outside the options should fail';
  EXCEPTION WHEN check_violation THEN NULL;
  END;
END $$;

-- ------------------------------------------------------------ shared daily trivia (0024)
-- One shared question per artist per day; a fan's copy must match it; shared questions never change.
DO $$
DECLARE artist uuid := '40000000-0000-0000-0000-000000000001'; d uuid; fan uuid := '49000000-0000-0000-0000-000000000001';
BEGIN
  INSERT INTO trivia_daily_questions (day, artist_id, kind, prompt, options, answer_index)
  VALUES ((now() AT TIME ZONE 'UTC')::date + 3, artist, 'track_album', 'Which album?', ARRAY['A', 'B', 'C'], 2)
  RETURNING id INTO d;
  BEGIN
    INSERT INTO trivia_daily_questions (day, artist_id, kind, prompt, options, answer_index)
    VALUES ((now() AT TIME ZONE 'UTC')::date + 3, artist, 'release_year', 'Again?', ARRAY['1', '2'], 0);
    RAISE EXCEPTION 'two shared questions for one artist and day should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, daily_question_id)
    VALUES (fan, (now() AT TIME ZONE 'UTC')::date + 3, artist, 'track_album', 'Which album?', ARRAY['A', 'B', 'C'], 0, d);
    RAISE EXCEPTION 'a copy with another answer should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%must match its shared daily question%', SQLERRM;
  END;
  INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, daily_question_id)
  VALUES (fan, (now() AT TIME ZONE 'UTC')::date + 3, artist, 'track_album', 'Which album?', ARRAY['A', 'B', 'C'], 2, d);
  BEGIN
    UPDATE trivia_daily_questions SET answer_index = 0 WHERE id = d;
    RAISE EXCEPTION 'changing a shared question should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%shared daily question never changes%', SQLERRM;
  END;
  BEGIN
    DELETE FROM trivia_daily_questions WHERE id = d;
    RAISE EXCEPTION 'deleting a shared question should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%shared daily question never changes%', SQLERRM;
  END;
END $$;

-- ------------------------------------------------------------ featured artist of the week (0023)
DO $$
DECLARE artist uuid := '40000000-0000-0000-0000-000000000001'; up uuid; monday date := date_trunc('week', now())::date;
BEGIN
  INSERT INTO featured_artists (week_start, artist_id, video_kind, video_url, spotify_track_id, track_title)
  VALUES (monday, artist, 'youtube', 'https://www.youtube.com/watch?v=abc', '1aaaaaaaaaaaaaaaaaaaaa', 'A song');
  BEGIN
    INSERT INTO featured_artists (week_start, artist_id, video_kind, video_url, spotify_track_id, track_title)
    VALUES (monday, artist, 'tiktok', 'https://www.tiktok.com/@x/video/1', '1aaaaaaaaaaaaaaaaaaaaa', 'Same week');
    RAISE EXCEPTION 'two picks in one week should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO featured_artists (week_start, artist_id, video_kind, video_url, spotify_track_id, track_title)
    VALUES (monday + 1, artist, 'youtube', 'https://www.youtube.com/watch?v=abc', '1aaaaaaaaaaaaaaaaaaaaa', 'Tuesday');
    RAISE EXCEPTION 'a week that starts on a Tuesday should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%featured_artists_monday%', SQLERRM;
  END;
  BEGIN
    INSERT INTO featured_artists (week_start, artist_id, video_kind, spotify_track_id, track_title)
    VALUES (monday + 7, artist, 'youtube', '1aaaaaaaaaaaaaaaaaaaaa', 'No link');
    RAISE EXCEPTION 'a link video without a link should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%featured_artists_video%', SQLERRM;
  END;
  -- An uploaded video must be a finished featured_video upload; photos stay JPEG/PNG up to 15 MB.
  INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes) VALUES ('00000000-0000-0000-0000-000000000001', 'featured_video', 'video/mp4', 50000000)
  RETURNING id INTO up;
  BEGIN
    INSERT INTO featured_artists (week_start, artist_id, video_kind, video_upload_id, spotify_track_id, track_title)
    VALUES (monday + 7, artist, 'upload', up, '1aaaaaaaaaaaaaaaaaaaaa', 'Not uploaded yet');
    RAISE EXCEPTION 'an unfinished upload should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%finished featured_video upload%', SQLERRM;
  END;
  UPDATE uploads SET storage_key = 'quarantine/' || up, sha256 = sha256('v'), bytes = 10, uploaded_at = now() WHERE id = up;
  INSERT INTO featured_artists (week_start, artist_id, video_kind, video_upload_id, spotify_track_id, track_title)
  VALUES (monday + 7, artist, 'upload', up, '1aaaaaaaaaaaaaaaaaaaaa', 'Uploaded');
  BEGIN
    INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes) VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'video/mp4', 10);
    RAISE EXCEPTION 'a video as proof should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%uploads_purpose_kind_check%', SQLERRM;
  END;
  BEGIN
    INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes) VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/jpeg', 20000000);
    RAISE EXCEPTION 'a 20 MB photo should fail';
  EXCEPTION WHEN check_violation THEN NULL;
  END;
END $$;

-- ------------------------------------------------------------ music prompts (0025)
-- One prompt a week (staff or library, out on Thursdays since 0034); one answer per fan, written or changed only in its
-- week; takedowns stay down.
INSERT INTO users (id, handle, email) VALUES
  ('4a000000-0000-0000-0000-000000000001', 'prompt_fan', 'prompt_fan@example.test'),
  ('4a000000-0000-0000-0000-000000000002', 'prompt_two', 'prompt_two@example.test');
DO $$
DECLARE fan uuid := '4a000000-0000-0000-0000-000000000001'; today date := prompt_week_day();
        p prompts; again prompts; old_p uuid; ans uuid;
BEGIN
  p := prompt_for_day(today);
  again := prompt_for_day(today);
  ASSERT p.id = again.id AND p.source = 'library', 'the library fills a day without a staff prompt, once';
  ASSERT (SELECT text FROM prompt_library ORDER BY position OFFSET ((today - date '2026-01-01') % 30 + 30) % 30 LIMIT 1) = p.text,
         'the library rotates by day';
  INSERT INTO prompts (day, text, source) VALUES (today + 7, 'Staff prompt', 'staff');
  ASSERT (prompt_for_day(today + 7)).text = 'Staff prompt', 'a staff prompt wins';

  INSERT INTO prompt_answers (prompt_id, user_id, body) VALUES (p.id, fan, 'The Velvet Ropes') RETURNING id INTO ans;
  BEGIN
    INSERT INTO prompt_answers (prompt_id, user_id, body) VALUES (p.id, fan, 'Second answer');
    RAISE EXCEPTION 'a second answer should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO prompt_answers (prompt_id, user_id, body, visibility) VALUES (p.id, '4a000000-0000-0000-0000-000000000002', 'Secret', 'private');
    RAISE EXCEPTION 'a private answer should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%prompt_answers_shared%', SQLERRM;
  END;
  BEGIN
    INSERT INTO prompt_answers (prompt_id, user_id, body) VALUES (p.id, '4a000000-0000-0000-0000-000000000002', '   ');
    RAISE EXCEPTION 'a blank answer should fail';
  EXCEPTION WHEN check_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO prompt_answers (prompt_id, user_id, body) VALUES ((prompt_for_day(today + 7)).id, fan, 'Too early');
    RAISE EXCEPTION 'answering next week''s prompt should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only in the prompt''s week%', SQLERRM;
  END;
  BEGIN
    UPDATE prompts SET text = 'Changed' WHERE id = p.id;
    RAISE EXCEPTION 'changing an answered prompt should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%fans have answered it%', SQLERRM;
  END;
  UPDATE prompt_answers SET body = 'The Velvet Ropes (edited)' WHERE id = ans;
  UPDATE prompt_answers SET removed_at = now() WHERE id = ans;
  BEGIN
    UPDATE prompt_answers SET removed_at = NULL WHERE id = ans;
    RAISE EXCEPTION 'restoring a taken-down answer should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%stays down%', SQLERRM;
  END;
  -- Reports and moderation decisions accept answers.
  INSERT INTO reports (reporter_id, target_type, target_id, reason) VALUES ('4a000000-0000-0000-0000-000000000002', 'prompt_answer', ans, 'spam');
  INSERT INTO moderation_actions (staff_id, target_type, target_id, action, reports) VALUES (NULL, 'prompt_answer', ans, 'take_down', 1);

  -- Last week's answers are fixed.
  INSERT INTO prompts (day, text, source) VALUES (today - 7, 'Last week', 'staff') RETURNING id INTO old_p;
  BEGIN
    INSERT INTO prompt_answers (prompt_id, user_id, body) VALUES (old_p, fan, 'Late');
    RAISE EXCEPTION 'answering last week''s prompt should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only in the prompt''s week%', SQLERRM;
  END;
END $$;

-- ------------------------------------------------------------ weekly trivia and prompts (0034)
-- Trivia comes out on Tuesdays and prompts on Thursdays: every day of a week maps to that week's day.
DO $$
BEGIN
  ASSERT trivia_week_day('2026-10-27') = '2026-10-27', 'a Tuesday is its own trivia day';
  ASSERT trivia_week_day('2026-11-02') = '2026-10-27', 'the Monday after still belongs to that Tuesday';
  ASSERT trivia_week_day('2026-10-26') = '2026-10-20', 'the Monday before belongs to the week before';
  ASSERT prompt_week_day('2026-10-29') = '2026-10-29', 'a Thursday is its own prompt day';
  ASSERT prompt_week_day('2026-11-04') = '2026-10-29', 'the Wednesday after still belongs to that Thursday';
  ASSERT prompt_week_day('2026-10-28') = '2026-10-22', 'the Wednesday before belongs to the week before';
  ASSERT extract(isodow FROM trivia_week_day()) = 2 AND extract(isodow FROM prompt_week_day()) = 4, 'today maps to a Tuesday and a Thursday';
END $$;

-- ------------------------------------------------------------ emoji reactions (0026)
-- Known emoji only, one of each per fan per post, pointing at a post that exists; deleting the post or the account
-- removes them.
INSERT INTO users (id, handle, email) VALUES
  ('4b000000-0000-0000-0000-000000000001', 'react_fan', 'react_fan@example.test'),
  ('4b000000-0000-0000-0000-000000000002', 'react_two', 'react_two@example.test');
DO $$
DECLARE fan uuid := '4b000000-0000-0000-0000-000000000001'; two uuid := '4b000000-0000-0000-0000-000000000002'; log_id uuid;
BEGIN
  INSERT INTO album_logs (user_id, album_id) VALUES (two, '10000000-0000-0000-0000-000000000001') RETURNING id INTO log_id;
  INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES ('log', log_id, fan, 'fire'), ('log', log_id, fan, 'heart');
  BEGIN
    INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES ('log', log_id, fan, 'fire');
    RAISE EXCEPTION 'the same emoji twice should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES ('log', log_id, fan, 'poop');
    RAISE EXCEPTION 'an unknown emoji should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%post_reactions_known%', SQLERRM;
  END;
  BEGIN
    INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES ('photo', log_id, fan, 'fire');
    RAISE EXCEPTION 'a reaction to a missing post should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE 'reaction: no photo with this id%', SQLERRM;
  END;
  INSERT INTO post_reactions (target_type, target_id, user_id, reaction) VALUES ('log', log_id, two, 'clap');
  UPDATE users SET deleted_at = now() WHERE id = fan;
  ASSERT NOT EXISTS (SELECT 1 FROM post_reactions WHERE user_id = fan), 'deleting an account removes its reactions';
  DELETE FROM album_logs WHERE id = log_id;
  ASSERT NOT EXISTS (SELECT 1 FROM post_reactions WHERE target_id = log_id), 'deleting a post removes its reactions';
END $$;

-- ------------------------------------------------------------ artist profile (0016)
-- Links need the artist flag; handles are bare; deletion clears the flag and links and they stay empty.
INSERT INTO users (id, handle, email, is_artist, band_url, instagram_handle, x_handle) VALUES
  ('00000000-0000-0000-0000-000000000013', 'gina', 'gina@example.test', true,
   'https://open.spotify.com/artist/abc', 'gina.band', 'ginaband');
SELECT pg_temp.expect_error(
  $$UPDATE users SET is_artist = false WHERE id = '00000000-0000-0000-0000-000000000013'$$, 'users_artist_links');
SELECT pg_temp.expect_error(
  $$UPDATE users SET band_url = 'http://example.test' WHERE id = '00000000-0000-0000-0000-000000000013'$$, 'users_band_url_check');
SELECT pg_temp.expect_error(
  $$UPDATE users SET instagram_handle = '@gina' WHERE id = '00000000-0000-0000-0000-000000000013'$$, 'users_instagram_handle_check');
SELECT pg_temp.expect_error(
  $$UPDATE users SET x_handle = 'this_handle_is_too_long' WHERE id = '00000000-0000-0000-0000-000000000013'$$, 'users_x_handle_check');
UPDATE users SET deleted_at = now() WHERE id = '00000000-0000-0000-0000-000000000013';
DO $$
DECLARE u users%ROWTYPE;
BEGIN
  SELECT * INTO u FROM users WHERE id = '00000000-0000-0000-0000-000000000013';
  ASSERT NOT u.is_artist AND u.band_url IS NULL AND u.instagram_handle IS NULL AND u.x_handle IS NULL,
         'deletion clears the artist flag and links';
END $$;
SELECT pg_temp.expect_error(
  $$UPDATE users SET is_artist = true WHERE id = '00000000-0000-0000-0000-000000000013'$$, 'personal fields stay empty');
DELETE FROM users WHERE id = '00000000-0000-0000-0000-000000000013';

-- ------------------------------------------------------------ follows and grab bags (0018)
INSERT INTO users (id, handle, email) VALUES
  ('00000000-0000-0000-0000-000000000014', 'hana', 'hana@example.test'),
  ('00000000-0000-0000-0000-000000000015', 'ivan', 'ivan@example.test'),
  ('00000000-0000-0000-0000-000000000016', 'jojo', 'jojo@example.test'),
  ('00000000-0000-0000-0000-000000000017', 'kim_gone', 'kim@example.test');
UPDATE users SET deleted_at = now() WHERE id = '00000000-0000-0000-0000-000000000017';
-- Following needs no approval and no self-follows; a deleted account can't follow or be followed.
INSERT INTO follows (follower_id, followee_id) VALUES
  ('00000000-0000-0000-0000-000000000014', '00000000-0000-0000-0000-000000000015'),
  ('00000000-0000-0000-0000-000000000015', '00000000-0000-0000-0000-000000000014'),
  ('00000000-0000-0000-0000-000000000016', '00000000-0000-0000-0000-000000000014');
SELECT pg_temp.expect_error(
  $$INSERT INTO follows (follower_id, followee_id) VALUES ('00000000-0000-0000-0000-000000000014', '00000000-0000-0000-0000-000000000014')$$,
  'follows_not_self');
SELECT pg_temp.expect_error(
  $$INSERT INTO follows (follower_id, followee_id) VALUES ('00000000-0000-0000-0000-000000000014', '00000000-0000-0000-0000-000000000017')$$,
  'deleted accounts cannot follow');
SELECT pg_temp.expect_error(
  $$INSERT INTO follows (follower_id, followee_id) VALUES ('00000000-0000-0000-0000-000000000017', '00000000-0000-0000-0000-000000000014')$$,
  'deleted accounts cannot follow');
SELECT pg_temp.expect_error(
  $$INSERT INTO follows (follower_id, followee_id) VALUES ('00000000-0000-0000-0000-000000000014', '00000000-0000-0000-0000-000000000015')$$,
  'follows_pkey');

-- Grab bag: five numbered slots, one row per song; alice is at today's artist limit (above) and can still track an
-- artist through her grab bag, which doesn't count toward the limit.
DO $$
DECLARE v_created boolean; v_id uuid; v_via text;
BEGIN
  SELECT o_artist_id, o_created INTO v_id, v_created
    FROM track_artist('00000000-0000-0000-0000-000000000001', 'sp-grab-bag-artist', 'Grab Bag Artist', 50, 'grab_bag');
  ASSERT v_created, 'a grab-bag add tracks a new artist even at the daily limit';
  SELECT added_via INTO v_via FROM artists WHERE id = v_id;
  ASSERT v_via = 'grab_bag', 'the add is recorded as grab_bag';
  INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
    VALUES ('00000000-0000-0000-0000-000000000001', 1, '0aaaaaaaaaaaaaaaaaaaa1', 'Song One', v_id),
           ('00000000-0000-0000-0000-000000000014', 5, '0aaaaaaaaaaaaaaaaaaaa2', 'Song Two', v_id);
END $$;
SELECT pg_temp.expect_error(
  $$SELECT track_artist('00000000-0000-0000-0000-000000000001', 'sp-bulk-52', 'Bulk 52')$$,
  'daily artist add limit reached');
SELECT pg_temp.expect_error(
  $$INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
    SELECT '00000000-0000-0000-0000-000000000014', 6, '0aaaaaaaaaaaaaaaaaaaa3', 'Six', id FROM artists WHERE spotify_artist_id = 'sp-grab-bag-artist'$$,
  'grab_bag_songs_position_check');
SELECT pg_temp.expect_error(
  $$INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
    SELECT '00000000-0000-0000-0000-000000000014', 5, '0aaaaaaaaaaaaaaaaaaaa3', 'Taken slot', id FROM artists WHERE spotify_artist_id = 'sp-grab-bag-artist'$$,
  'grab_bag_songs_user_id_position_key');
SELECT pg_temp.expect_error(
  $$INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
    SELECT '00000000-0000-0000-0000-000000000014', 4, '0aaaaaaaaaaaaaaaaaaaa2', 'Same song twice', id FROM artists WHERE spotify_artist_id = 'sp-grab-bag-artist'$$,
  'grab_bag_songs_user_id_spotify_track_id_key');
SELECT pg_temp.expect_error(
  $$INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
    SELECT '00000000-0000-0000-0000-000000000014', 3, 'not-a-spotify-id', 'Bad id', id FROM artists WHERE spotify_artist_id = 'sp-grab-bag-artist'$$,
  'grab_bag_songs_spotify_track_id_check');

-- Deleting an account removes its follows both ways and its grab bag.
UPDATE users SET deleted_at = now() WHERE id = '00000000-0000-0000-0000-000000000014';
DO $$ BEGIN
  ASSERT NOT EXISTS (SELECT 1 FROM follows WHERE follower_id = '00000000-0000-0000-0000-000000000014'
                                               OR followee_id = '00000000-0000-0000-0000-000000000014'),
         'deletion removes follows both ways';
  ASSERT NOT EXISTS (SELECT 1 FROM follows WHERE follower_id = '00000000-0000-0000-0000-000000000016'),
         'jojo''s follow of the deleted account is gone too';
  ASSERT NOT EXISTS (SELECT 1 FROM grab_bag_songs WHERE user_id = '00000000-0000-0000-0000-000000000014'), 'deletion empties the grab bag';
  ASSERT EXISTS (SELECT 1 FROM grab_bag_songs WHERE user_id = '00000000-0000-0000-0000-000000000001'), 'other grab bags stay';
END $$;

-- ------------------------------------------------------------ storage cleanup (0027)
-- Removing a row that names a stored file queues its key in storage_deletions, once, in the same transaction.
DELETE FROM storage_deletions;
DO $$
DECLARE up uuid; sub uuid := '40000000-0000-0000-0000-0000000000c1';
BEGIN
  -- An unattached slot without a file queues nothing; one with a file queues its key.
  INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes) VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/jpeg', 10)
    RETURNING id INTO up;
  DELETE FROM uploads WHERE id = up;
  ASSERT NOT EXISTS (SELECT 1 FROM storage_deletions), 'a slot with no file queues nothing';
  INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at)
    VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/jpeg', 10, 'quarantine/inv-slot', sha256('s'), 10, now())
    RETURNING id INTO up;
  DELETE FROM uploads WHERE id = up;
  ASSERT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = 'quarantine/inv-slot'), 'a deleted slot queues its key';

  -- A submission's file and its attached slot name the same key: deleting the submission queues it once.
  INSERT INTO submissions (id, user_id, artist_id, kind)
    SELECT sub, '00000000-0000-0000-0000-000000000001', id, 'merch' FROM artists WHERE spotify_artist_id = 'sp-active';
  INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at, submission_id)
    VALUES ('00000000-0000-0000-0000-000000000001', 'submission', 'image/jpeg', 10, 'quarantine/inv-sub', sha256('t'), 10, now(), sub);
  INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
    VALUES (sub, 'quarantine/inv-sub', sha256('t'), 'image/jpeg', 10), (sub, 'quarantine/inv-sub-2', sha256('u'), 'image/jpeg', 10);
  ASSERT NOT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key LIKE 'quarantine/inv-sub%'), 'live files are not queued';

  -- A changed key queues the old one.
  UPDATE submission_files SET storage_key = 'quarantine/inv-sub-2b' WHERE storage_key = 'quarantine/inv-sub-2';
  ASSERT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = 'quarantine/inv-sub-2'), 'a replaced key is queued';

  DELETE FROM submissions WHERE id = sub;
  ASSERT (SELECT count(*) FROM storage_deletions WHERE storage_key = 'quarantine/inv-sub') = 1, 'a shared key is queued once';
  ASSERT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = 'quarantine/inv-sub-2b'), 'every file of the submission is queued';
END $$;
SELECT pg_temp.expect_error($$INSERT INTO storage_deletions (storage_key) VALUES ('')$$, 'storage_deletions_storage_key_check');
DELETE FROM storage_deletions;

-- ------------------------------------------------------------ rejected file retention (0028)
-- rejected_at is stamped when a submission becomes rejected and cleared when it stops being rejected.
DO $$
DECLARE sub uuid := '40000000-0000-0000-0000-0000000000c2'; first_stamp timestamptz;
BEGIN
  INSERT INTO submissions (id, user_id, artist_id, kind)
    SELECT sub, '00000000-0000-0000-0000-000000000001', id, 'merch' FROM artists WHERE spotify_artist_id = 'sp-active';
  ASSERT (SELECT rejected_at FROM submissions WHERE id = sub) IS NULL, 'a new submission is not rejected';
  UPDATE submissions SET status = 'rejected', rejection_reason = 'test' WHERE id = sub;
  SELECT rejected_at INTO first_stamp FROM submissions WHERE id = sub;
  ASSERT first_stamp IS NOT NULL, 'rejecting stamps rejected_at';
  UPDATE submissions SET rejected_at = first_stamp - interval '40 days' WHERE id = sub;
  UPDATE submissions SET status = 'rejected' WHERE id = sub;
  ASSERT (SELECT rejected_at FROM submissions WHERE id = sub) = first_stamp - interval '40 days',
         'staying rejected keeps the original stamp';
  UPDATE submissions SET status = 'needs_review' WHERE id = sub;
  ASSERT (SELECT rejected_at FROM submissions WHERE id = sub) IS NULL, 'leaving rejected clears the stamp';
  DELETE FROM submissions WHERE id = sub;
END $$;
DELETE FROM storage_deletions;

-- ------------------------------------------------------------ redaction and archive (0029)
-- Verifying queues the redaction once; archive rows are redacted, under archive/, with a time; a file sent to staff
-- notifies every live staff account once.
DO $$
DECLARE sub uuid := '40000000-0000-0000-0000-0000000000c3'; f uuid; staff_count int;
BEGIN
  DELETE FROM jobs WHERE kind = 'submissions.redact';
  INSERT INTO submissions (id, user_id, artist_id, kind)
    SELECT sub, '00000000-0000-0000-0000-000000000001', id, 'ticket' FROM artists WHERE spotify_artist_id = 'sp-active';
  INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
    VALUES (sub, 'quarantine/inv-redact', sha256('r'), 'image/jpeg', 10) RETURNING id INTO f;
  ASSERT (SELECT redaction_state FROM submission_files WHERE id = f) = 'pending', 'new files wait for redaction';
  UPDATE submissions SET status = 'verified' WHERE id = sub;
  UPDATE submissions SET status = 'verified' WHERE id = sub;
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'submissions.redact' AND dedupe_key = 'redact:' || sub) = 1,
         'verifying queues one redaction';

  BEGIN
    UPDATE submission_files SET tier = 'archive', redacted = true, archived_at = now(), redaction_state = 'archived' WHERE id = f;
    RAISE EXCEPTION 'an archive row under a quarantine key should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%submission_files_archive_consistent%', SQLERRM;
  END;
  BEGIN
    UPDATE submission_files SET storage_key = 'archive/inv-redact', tier = 'archive', redacted = true, archived_at = now() WHERE id = f;
    RAISE EXCEPTION 'an archive row not marked archived should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%submission_files_archive_consistent%', SQLERRM;
  END;
  BEGIN
    UPDATE submission_files SET storage_key = 'archive/inv-redact', redaction_state = 'archived' WHERE id = f;
    RAISE EXCEPTION 'an archived row in the hot tier should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%submission_files_archive_consistent%', SQLERRM;
  END;

  SELECT count(*) INTO staff_count FROM users WHERE is_staff AND deleted_at IS NULL;
  UPDATE submission_files SET redaction_state = 'needs_review', redaction_note = 'No code found' WHERE id = f;
  UPDATE submission_files SET redaction_state = 'approved' WHERE id = f;
  UPDATE submission_files SET redaction_state = 'needs_review' WHERE id = f;
  ASSERT (SELECT count(*) FROM notifications WHERE kind = 'staff_redaction' AND subject_id = f) = staff_count,
         'each staff account hears about a file once';

  UPDATE submission_files SET storage_key = 'archive/inv-redact', tier = 'archive', redacted = true, archived_at = now(),
                              redaction_state = 'archived' WHERE id = f;
  ASSERT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = 'quarantine/inv-redact'), 'archiving queues the quarantined original';
  DELETE FROM submissions WHERE id = sub;
  DELETE FROM notifications WHERE kind = 'staff_redaction';
  DELETE FROM jobs WHERE kind = 'submissions.redact';
END $$;
SELECT pg_temp.expect_error($$UPDATE submission_files SET redaction_state = 'lost'$$, 'submission_files_redaction_state_check');
DELETE FROM storage_deletions;

-- ------------------------------------------------------------ concert sources (0030)
-- One source id per artist and per source id; a match needs both id and how it was matched; shows carry a known status
-- and an https ticket link; hub_city_for matches name, region, and country.
DO $$
DECLARE a uuid;
BEGIN
  SELECT id INTO a FROM artists WHERE spotify_artist_id = 'sp-active';
  INSERT INTO artist_sources (artist_id, source, external_id, matched_by) VALUES (a, 'ticketmaster', 'K8vZinv1', 'staff');
  BEGIN
    INSERT INTO artist_sources (artist_id, source, external_id, matched_by)
      SELECT id, 'ticketmaster', 'K8vZinv1', 'staff' FROM artists WHERE id <> a LIMIT 1;
    RAISE EXCEPTION 'two artists on one Ticketmaster id should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    UPDATE artist_sources SET matched_by = NULL WHERE artist_id = a;
    RAISE EXCEPTION 'an id without how it was matched should fail';
  EXCEPTION WHEN check_violation THEN ASSERT SQLERRM LIKE '%artist_sources_match%', SQLERRM;
  END;
  DELETE FROM artist_sources WHERE artist_id = a;

  ASSERT hub_city_for(' chicago', 'il', 'us') = 'us-il-chicago', 'hub cities match without case or spaces';
  ASSERT hub_city_for('Chicago', 'IN', 'US') IS NULL, 'the region must match too';
  ASSERT hub_city_for('Rosemont', 'IL', 'US') IS NULL, 'a suburb has no hub city';
END $$;
SELECT pg_temp.expect_error($$UPDATE events SET status = 'sold_out'$$, 'events_status_check');
SELECT pg_temp.expect_error($$UPDATE events SET ticket_url = 'http://example.com'$$, 'events_ticket_url_check');

-- ------------------------------------------------------------ presale reminders (0031)
-- A presale's notification id is stable across syncs (same show, name, and start) and differs otherwise; reminders
-- default on; 'presale' is a known notification kind.
DO $$
DECLARE e uuid := gen_random_uuid();
BEGIN
  ASSERT presale_subject_id(e, 'Verified Fan', '2026-11-01T15:00:00Z') = presale_subject_id(e, 'Verified Fan', '2026-11-01T15:00:00Z'),
         'the same presale gets the same id';
  ASSERT presale_subject_id(e, 'Verified Fan', '2026-11-01T15:00:00Z') <> presale_subject_id(e, 'Verified Fan', '2026-11-02T15:00:00Z'),
         'another start is another presale';
  ASSERT (SELECT presale_reminders FROM users WHERE id = '00000000-0000-0000-0000-000000000001'), 'reminders default on';
END $$;
SELECT pg_temp.expect_error(
  $$INSERT INTO notifications (user_id, kind, subject_id) VALUES ('00000000-0000-0000-0000-000000000001', 'presale_spam', gen_random_uuid())$$,
  'notifications_kind_check');

-- ------------------------------------------------------------ push notifications (0032)
-- Expo tokens only, one row per token; a notification for a fan with a live device queues its push once; a deleted
-- account loses its devices.
DO $$
DECLARE u uuid; n uuid;
BEGIN
  INSERT INTO users (handle, email) VALUES ('push_fan', 'push_fan@example.test') RETURNING id INTO u;
  INSERT INTO notifications (user_id, kind, subject_id) VALUES (u, 'ticket_delivered', gen_random_uuid()) RETURNING id INTO n;
  ASSERT NOT EXISTS (SELECT 1 FROM jobs WHERE dedupe_key = 'push:' || n), 'no device, no push job';
  INSERT INTO push_devices (user_id, token, platform) VALUES (u, 'ExponentPushToken[inv-token-0001]', 'ios');
  BEGIN
    INSERT INTO push_devices (user_id, token, platform) VALUES (u, 'ExponentPushToken[inv-token-0001]', 'android');
    RAISE EXCEPTION 'a token twice should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  INSERT INTO notifications (user_id, kind, subject_id) VALUES (u, 'ticket_delivered', gen_random_uuid()) RETURNING id INTO n;
  ASSERT (SELECT count(*) FROM jobs WHERE kind = 'notifications.push' AND dedupe_key = 'push:' || n) = 1, 'a live device queues the push';
  UPDATE users SET deleted_at = now() WHERE id = u;
  ASSERT NOT EXISTS (SELECT 1 FROM push_devices WHERE user_id = u), 'deleting the account removes its devices';
  DELETE FROM jobs WHERE kind = 'notifications.push';
END $$;
SELECT pg_temp.expect_error(
  $$INSERT INTO push_devices (user_id, token, platform) VALUES ('00000000-0000-0000-0000-000000000001', 'fcm:abc', 'android')$$,
  'push_devices_token_check');

-- ------------------------------------------------------------ city time zones and the rules lock (0033)
-- Every hub city has a zone PostgreSQL knows; a new or changed city must too.
DO $$
BEGIN
  ASSERT NOT EXISTS (SELECT 1 FROM cities WHERE time_zone NOT IN (SELECT name FROM pg_timezone_names)),
         'every city has a known time zone';
  ASSERT (SELECT time_zone FROM cities WHERE id = 'us-tx-el-paso') = 'America/Denver', 'El Paso is on Mountain time';
  ASSERT (SELECT time_zone FROM cities WHERE id = 'us-tn-knoxville') = 'America/New_York', 'Knoxville is on Eastern time';
END $$;
SELECT pg_temp.expect_error($$UPDATE cities SET time_zone = 'America/Atlantis' WHERE id = 'us-il-chicago'$$, 'not a known time zone');
SELECT pg_temp.expect_error($$INSERT INTO cities (id, name, region, country) VALUES ('us-xx-nowhere', 'Nowhere', 'XX', 'US')$$, 'time_zone');

-- An allocation with a registration is locked even while its window hasn't opened (a registration can only get in
-- early by an insert outside register_for_allocation(); the lock no longer depends on the clock alone).
DO $$
DECLARE ev uuid; al uuid;
BEGIN
  INSERT INTO events (artist_id, name, city_id, city, country, starts_at)
  VALUES ('40000000-0000-0000-0000-000000000001', 'Lock show', 'us-il-chicago', 'Chicago', 'US', now() + interval '14 days')
  RETURNING id INTO ev;
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES (ev, 10, 4500, now() + interval '1 day', now() + interval '5 days', now())
  RETURNING id INTO al;
  UPDATE allocations SET tickets = 12 WHERE id = al;
  ASSERT (SELECT tickets FROM allocations WHERE id = al) = 12, 'rules editable before opening with nobody registered';

  INSERT INTO allocation_registrations (allocation_id, event_id, user_id, hub_city_ids, hub_match, percentile, raw_score,
                                        is_estimated, baseline_weight, score_computed_at)
  VALUES (al, ev, '00000000-0000-0000-0000-000000000001', '{us-il-chicago}', 1, 90, 50, false, 0.2, now());
  BEGIN
    UPDATE allocations SET tickets = 5 WHERE id = al;
    RAISE EXCEPTION 'changing an allocation someone registered for should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%rules are locked%', SQLERRM;
  END;
  -- Cancelling still works.
  UPDATE allocations SET cancelled_at = now() WHERE id = al;
END $$;

-- ------------------------------------------------------------ trivia open until answered (0035)
-- The current question is the unanswered weekly one, else the one answered since this week's Tuesday; a question left
-- from the daily days is skipped; one answered before this week means it's time for a new one.
INSERT INTO users (id, handle, email) VALUES ('4c000000-0000-0000-0000-000000000001', 'trivia_open', 'trivia_open@example.test');
DO $$
DECLARE fan uuid := '4c000000-0000-0000-0000-000000000001'; artist uuid; weekly uuid; daily uuid;
BEGIN
  SELECT id INTO artist FROM artists WHERE spotify_artist_id = 'sp-active';
  ASSERT current_trivia_question(fan) IS NULL, 'no question yet';
  INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
  VALUES (fan, trivia_week_day() - 7, artist, 'track_album', 'Last week', ARRAY['A', 'B'], 0) RETURNING id INTO weekly;
  INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
  VALUES (fan, trivia_week_day() - 8, artist, 'track_album', 'Daily leftover', ARRAY['A', 'B'], 0) RETURNING id INTO daily;
  ASSERT current_trivia_question(fan) = weekly, 'last week''s unanswered question is still the current one';
  PERFORM answer_trivia(fan, weekly, 0);
  ASSERT current_trivia_question(fan) = weekly, 'answered this week: its results stay until the next Tuesday';
  ALTER TABLE trivia_questions DISABLE TRIGGER trg_trivia_guard;
  UPDATE trivia_questions SET answered_at = trivia_week_day()::timestamp AT TIME ZONE 'UTC' - interval '1 hour' WHERE id = weekly;
  ALTER TABLE trivia_questions ENABLE TRIGGER trg_trivia_guard;
  ASSERT current_trivia_question(fan) IS NULL, 'answered before this week, with only a daily leftover: a new question is due';
  DELETE FROM trivia_questions WHERE user_id = fan;
END $$;

-- ------------------------------------------------------------ the draw as a job (0036)
-- allocation_selection_blocker() says why the draw can't run, with the same rules as run_allocation_selection().
DO $$
DECLARE ev uuid; al uuid;
BEGIN
  INSERT INTO events (artist_id, name, city_id, city, country, starts_at)
  VALUES ('40000000-0000-0000-0000-000000000001', 'Blocker show', 'us-il-chicago', 'Chicago', 'US', now() + interval '14 days')
  RETURNING id INTO ev;
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at)
  VALUES (ev, 10, 4500, now() - interval '1 day', now() + interval '5 days') RETURNING id INTO al;
  ASSERT allocation_selection_blocker(al) = 'not_found', 'a draft is not found';
  ASSERT allocation_selection_blocker(gen_random_uuid()) = 'not_found', 'an unknown allocation is not found';
  UPDATE allocations SET published_at = now() WHERE id = al;
  ASSERT allocation_selection_blocker(al) = 'registration_open', 'open registration blocks the draw';
  ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
  UPDATE allocations SET registration_closes_at = now() - interval '1 minute' WHERE id = al;
  ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
  ASSERT allocation_selection_blocker(al) IS NULL, 'closed registration lets the draw run';
  PERFORM run_allocation_selection(al, NULL);
  ASSERT allocation_selection_blocker(al) IS NULL, 'a draw that has run is not blocked (the API answers with it)';
  INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
  VALUES (ev, 10, 4500, now() - interval '1 day', now() + interval '5 days', now()) RETURNING id INTO al;
  UPDATE allocations SET cancelled_at = now() WHERE id = al;
  ASSERT allocation_selection_blocker(al) = 'cancelled', 'a cancelled allocation blocks the draw';
END $$;

-- ------------------------------------------------------------ organizations (0037)
-- Every membership change is logged with its actor; the log is append-only; an organization keeps an owner, except
-- that deleting an account always goes through.
DO $$
DECLARE org uuid; owner_id uuid; admin_id uuid; staff_id uuid;
BEGIN
  INSERT INTO users (handle, email) VALUES ('org_owner', 'org_owner@example.test') RETURNING id INTO owner_id;
  INSERT INTO users (handle, email) VALUES ('org_admin', 'org_admin@example.test') RETURNING id INTO admin_id;
  INSERT INTO users (handle, email, is_staff) VALUES ('org_staff', 'org_staff@example.test', true) RETURNING id INTO staff_id;
  INSERT INTO organizations (name, created_by) VALUES ('Invariant Presents', staff_id) RETURNING id INTO org;

  PERFORM set_config('fandom.actor', staff_id::text, true);
  INSERT INTO organization_members (organization_id, user_id, role, added_by) VALUES (org, owner_id, 'owner', staff_id);
  PERFORM set_config('fandom.actor', owner_id::text, true);
  INSERT INTO organization_members (organization_id, user_id, role, added_by) VALUES (org, admin_id, 'analyst', owner_id);
  UPDATE organization_members SET role = 'admin' WHERE organization_id = org AND user_id = admin_id;
  ASSERT (SELECT array_agg(action || ':' || COALESCE(old_role, '-') || '>' || COALESCE(new_role, '-') ORDER BY id)
            FROM organization_audit WHERE organization_id = org)
         = ARRAY['member_added:->owner', 'member_added:->analyst', 'role_changed:analyst>admin'], 'membership changes are logged';
  ASSERT (SELECT actor_id FROM organization_audit WHERE organization_id = org ORDER BY id LIMIT 1) = staff_id, 'the actor is recorded';

  BEGIN
    UPDATE organization_audit SET action = 'member_removed' WHERE organization_id = org;
    RAISE EXCEPTION 'editing the audit log should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never changed or deleted%', SQLERRM;
  END;
  BEGIN
    DELETE FROM organization_audit WHERE organization_id = org;
    RAISE EXCEPTION 'deleting from the audit log should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never changed or deleted%', SQLERRM;
  END;

  BEGIN
    UPDATE organization_members SET role = 'admin' WHERE organization_id = org AND user_id = owner_id;
    RAISE EXCEPTION 'demoting the last owner should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%must keep an owner%', SQLERRM;
  END;
  BEGIN
    DELETE FROM organization_members WHERE organization_id = org AND user_id = owner_id;
    RAISE EXCEPTION 'removing the last owner should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%must keep an owner%', SQLERRM;
  END;

  -- With a second owner, the first can step down.
  UPDATE organization_members SET role = 'owner' WHERE organization_id = org AND user_id = admin_id;
  UPDATE organization_members SET role = 'analyst' WHERE organization_id = org AND user_id = owner_id;

  -- Deleting the (now only) owner's account still goes through and is logged as such.
  PERFORM set_config('fandom.actor', '', true);
  UPDATE users SET deleted_at = now() WHERE id = admin_id;
  ASSERT NOT EXISTS (SELECT 1 FROM organization_members WHERE user_id = admin_id), 'a deleted account leaves its organizations';
  ASSERT (SELECT action FROM organization_audit WHERE organization_id = org AND user_id = admin_id ORDER BY id DESC LIMIT 1)
         = 'account_deleted', 'account deletion is logged';
END $$;
SELECT pg_temp.expect_error($$INSERT INTO organization_members (organization_id, user_id, role)
  SELECT id, '00000000-0000-0000-0000-000000000001', 'boss' FROM organizations LIMIT 1$$, 'organization_members_role_check');
SELECT pg_temp.expect_error($$INSERT INTO organizations (name) VALUES ('invariant presents')$$, 'organizations_name_uidx');
SELECT pg_temp.expect_error($$INSERT INTO organizations (name, kind) VALUES ('Label Co', 'label')$$, 'organizations_kind_check');

-- ------------------------------------------------------------ show change alerts (0036)
-- A source change to a show with a live allocation notifies staff once per change; other shows notify nobody.
DO $$
DECLARE e_alloc uuid; e_plain uuid; artist uuid; staff_count int; n int;
BEGIN
  SELECT id INTO artist FROM artists WHERE spotify_artist_id = 'sp-active';
  SELECT count(*) INTO staff_count FROM users WHERE is_staff AND deleted_at IS NULL;
  ASSERT staff_count > 0, 'the fixtures have a staff account';
  INSERT INTO events (artist_id, name, city_id, starts_at, source_starts_at) VALUES
    (artist, 'Alloc show', 'us-il-chicago', now() + interval '60 days', now() + interval '60 days') RETURNING id INTO e_alloc;
  INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (artist, 'Plain show', 'us-il-chicago', now() + interval '60 days')
    RETURNING id INTO e_plain;
  INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at)
    VALUES (e_alloc, 5, 3000, 0, now() + interval '1 day', now() + interval '2 days');
  UPDATE events SET source_starts_at = now() + interval '61 days', status = 'rescheduled' WHERE id IN (e_alloc, e_plain);
  UPDATE events SET status = 'rescheduled' WHERE id = e_alloc;   -- no change: no second notice
  SELECT count(*) INTO n FROM notifications WHERE kind = 'staff_show_changed';
  ASSERT n = staff_count, format('one notice per staff account for the allocated show (%s, expected %s)', n, staff_count);
  DELETE FROM notifications WHERE kind = 'staff_show_changed';
  DELETE FROM allocations WHERE event_id = e_alloc;
  DELETE FROM events WHERE id IN (e_alloc, e_plain);
END $$;

-- ------------------------------------------------------------ link clicks (0038)
-- Known kinds; a show only (and always) for ticket clicks; artist links are https.
SELECT pg_temp.expect_error(
  $$INSERT INTO link_clicks (kind, artist_id) SELECT 'purchase', id FROM artists WHERE spotify_artist_id = 'sp-active'$$,
  'link_clicks_kind_check');
SELECT pg_temp.expect_error(
  $$INSERT INTO link_clicks (kind, artist_id) SELECT 'event_tickets', id FROM artists WHERE spotify_artist_id = 'sp-active'$$,
  'link_clicks_event_kind');
SELECT pg_temp.expect_error($$UPDATE artists SET website_url = 'http://example.com' WHERE spotify_artist_id = 'sp-active'$$, 'artists_website_https');
SELECT pg_temp.expect_error($$UPDATE artists SET merch_url = 'javascript:alert(1)' WHERE spotify_artist_id = 'sp-active'$$, 'artists_merch_https');

-- ------------------------------------------------------------ fan levels and trust (0039)
-- A stored level always matches its activity; trust snapshots are written only by the draw.
SELECT pg_temp.expect_error(
  $$INSERT INTO fan_overall_scores (user_id, trust, trust_parts, activity, activity_parts, level)
    VALUES ('00000000-0000-0000-0000-000000000001', 0.5, '{}', 0.9, '{}', 2)$$,
  'fan_overall_scores_level_matches');
DO $$
BEGIN
  ASSERT parts_average('{"a": 1, "b": 0, "c": 0.5}') = 0.5, 'parts weigh equally';
  ASSERT parts_average('{}') = 0, 'no parts is zero';
  ASSERT (SELECT count(*) FROM jsonb_object_keys(fan_trust_parts('00000000-0000-0000-0000-000000000001'))) = 4, 'four trust parts';
  ASSERT (SELECT count(*) FROM jsonb_object_keys(fan_activity_parts('00000000-0000-0000-0000-000000000001'))) = 5, 'five activity parts';
END $$;
SELECT pg_temp.expect_error(
  $$INSERT INTO allocation_trust_snapshots (allocation_id, registration_id, trust) VALUES (gen_random_uuid(), gen_random_uuid(), 1)$$,
  'written once by the draw');

-- ------------------------------------------------------------ ten fan levels (0040)
DO $$
BEGIN
  ASSERT fan_level_for(0.05) = 1 AND fan_level_for(0.1) = 2 AND fan_level_for(0.95) = 10 AND fan_level_for(1) = 10, 'ten levels, a tenth each';
END $$;
SELECT pg_temp.expect_error(
  $$INSERT INTO fan_overall_scores (user_id, trust, trust_parts, activity, activity_parts, level)
    VALUES ('00000000-0000-0000-0000-000000000001', 0.5, '{}', 0.95, '{}', 11)$$,
  'fan_overall_scores_level');

-- ------------------------------------------------------------ +1 guest decline (0041)
-- Only decline_plus_one_invite() clears a named guest before the draw (RegistrationApiTests covers the guard).
DO $$
BEGIN
  ASSERT decline_plus_one_invite(gen_random_uuid(), gen_random_uuid()) = 0, 'declining with nothing to decline is a no-op';
END $$;

-- ------------------------------------------------------------ fraud review (0042)
-- A flag's facts never change and its status only moves forward; one open flag per fan; an appeal is the flagged fan's,
-- once, while the flag holds them; deciding an appeal decides the flag; the audit log is append-only and records each
-- step with its actor. (FraudReviewApiTests covers registering, the draw, and returned seats.)
DO $$
DECLARE fan uuid; other uuid; staff uuid; f uuid; ap uuid; v_actions text[];
BEGIN
  INSERT INTO users (handle, email) VALUES ('fraud_fan', 'fraud_fan@example.test') RETURNING id INTO fan;
  INSERT INTO users (handle, email) VALUES ('fraud_other', 'fraud_other@example.test') RETURNING id INTO other;
  INSERT INTO users (handle, email, is_staff) VALUES ('fraud_reviewer', 'fraud_reviewer@example.test', true) RETURNING id INTO staff;

  ASSERT NOT fan_on_hold(fan), 'no flag, no hold';
  INSERT INTO fraud_flags (user_id, reason, raised_by) VALUES (fan, 'bot', staff) RETURNING id INTO f;
  ASSERT fan_on_hold(fan) AND fan_hold_flag(fan) = f, 'an open flag holds the fan';
  ASSERT EXISTS (SELECT 1 FROM notifications WHERE user_id = fan AND kind = 'fraud_hold' AND subject_id = f), 'the fan is told';

  BEGIN
    INSERT INTO fraud_flags (user_id, reason, raised_by) VALUES (fan, 'other', staff);
    RAISE EXCEPTION 'a second open flag should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    UPDATE fraud_flags SET reason = 'other' WHERE id = f;
    RAISE EXCEPTION 'editing a flag''s facts should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only the decision changes%', SQLERRM;
  END;
  BEGIN
    UPDATE fraud_flags SET status = 'upheld' WHERE id = f;
    RAISE EXCEPTION 'a decision without a decider should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%records who made it%', SQLERRM;
  END;
  BEGIN
    DELETE FROM fraud_flags WHERE id = f;
    RAISE EXCEPTION 'deleting a flag should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never deleted%', SQLERRM;
  END;
  BEGIN
    INSERT INTO fraud_appeals (flag_id, user_id, message) VALUES (f, other, 'not mine');
    RAISE EXCEPTION 'appealing someone else''s flag should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%only the flagged fan%', SQLERRM;
  END;

  INSERT INTO fraud_appeals (flag_id, user_id, message) VALUES (f, fan, 'I am a person') RETURNING id INTO ap;
  ASSERT EXISTS (SELECT 1 FROM notifications n JOIN users u ON u.id = n.user_id
                  WHERE u.id = staff AND n.kind = 'staff_appeal' AND n.subject_id = ap), 'staff are told';
  BEGIN
    INSERT INTO fraud_appeals (flag_id, user_id, message) VALUES (f, fan, 'again');
    RAISE EXCEPTION 'a second appeal should fail';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;

  PERFORM decide_fraud_appeal(ap, staff, false, 'No');
  ASSERT (SELECT status FROM fraud_flags WHERE id = f) = 'upheld', 'a denied appeal upholds the flag';
  BEGIN
    UPDATE fraud_appeals SET status = 'granted', decided_by = staff WHERE id = ap;
    RAISE EXCEPTION 'deciding an appeal twice should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%once%', SQLERRM;
  END;
  BEGIN
    PERFORM decide_fraud_appeal(ap, staff, true, NULL);
    RAISE EXCEPTION 'deciding an appeal twice should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%already_decided%', SQLERRM;
  END;

  UPDATE fraud_flags SET status = 'cleared', decided_by = staff, decision_note = 'second look' WHERE id = f;
  ASSERT NOT fan_on_hold(fan), 'a cleared flag holds no one';
  BEGIN
    UPDATE fraud_flags SET status = 'upheld', decided_by = staff WHERE id = f;
    RAISE EXCEPTION 'reopening a cleared flag should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%can''t become%', SQLERRM;
  END;

  SELECT array_agg(action || ':' || COALESCE((SELECT handle::text FROM users WHERE id = actor_id), '-') ORDER BY id)
    INTO v_actions FROM eligibility_audit WHERE user_id = fan;
  ASSERT v_actions = ARRAY['flag_raised:fraud_reviewer', 'appeal_filed:fraud_fan', 'appeal_denied:fraud_reviewer',
                           'flag_upheld:fraud_reviewer', 'flag_cleared:fraud_reviewer'], array_to_string(v_actions, ', ');
  ASSERT (SELECT inputs ->> 'note' FROM eligibility_audit WHERE flag_id = f AND action = 'flag_cleared') = 'second look',
         'a decision keeps its note in the log';

  BEGIN
    UPDATE eligibility_audit SET reason = 'x' WHERE user_id = fan;
    RAISE EXCEPTION 'editing the eligibility audit should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never changed or deleted%', SQLERRM;
  END;
  BEGIN
    DELETE FROM eligibility_audit WHERE user_id = fan;
    RAISE EXCEPTION 'deleting from the eligibility audit should fail';
  EXCEPTION WHEN OTHERS THEN ASSERT SQLERRM LIKE '%never changed or deleted%', SQLERRM;
  END;
END $$;
SELECT pg_temp.expect_error($$INSERT INTO fraud_flags (user_id, reason, raised_by)
  VALUES ('00000000-0000-0000-0000-000000000001', 'vibes', '00000000-0000-0000-0000-000000000002')$$, 'fraud_flags_reason_check');
SELECT pg_temp.expect_error($$INSERT INTO fraud_flags (user_id, reason)
  VALUES ('00000000-0000-0000-0000-000000000001', 'bot')$$, 'fraud_flags_raised_by_check');
SELECT pg_temp.expect_error($$INSERT INTO eligibility_audit (user_id, action)
  VALUES ('00000000-0000-0000-0000-000000000001', 'registration_blocked')$$, 'eligibility_audit_subject_check');
SELECT pg_temp.expect_error($$TRUNCATE eligibility_audit CASCADE$$, 'never changed or deleted');

\echo ALL INVARIANT TESTS PASSED
