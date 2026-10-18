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

SELECT pg_temp.expect_error(
  $$UPDATE artists SET status = 'active' WHERE spotify_artist_id = 'sp-active'$$,
  'needs a baseline before activation');
INSERT INTO artist_baselines (artist_id, dist_params, curve)
  SELECT id, '{}', '[]' FROM artists WHERE spotify_artist_id = 'sp-active';
UPDATE artists SET status = 'active' WHERE spotify_artist_id = 'sp-active';
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

\echo ALL INVARIANT TESTS PASSED
