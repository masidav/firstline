-- Privacy review (2026-11-01, services/api/src/features/privacy/DATA_INVENTORY.md).
--
-- 1. Account deletion left personal data behind for good: the play ledger and saved albums (only signals and liked
--    tracks were dropped), the overall trust/activity score until the next daily refresh, +1 declines, cached API
--    responses (idempotency keys, pruned only when the same fan made another request), the profile picture's file
--    (unlinked at once but kept until the 30-day purge), and the fan's handle copied into other people's notices
--    (staff "proof to review", "flagged before the draw", "appealed"; a +1's "named you"). Deletion now removes or
--    scrubs all of these at once.
-- 2. The purge (30 days later) also deletes the fan's trivia questions and answers (content, like prompt answers) and
--    repeats the listening-data deletes, in case a Spotify poll that started before the deletion wrote after it.
-- 3. Housekeeping retention (no product effect), run daily by the worker's privacy.retention job:
--    sessions that expired or were revoked more than 30 days ago (with their refresh tokens), idempotency keys after
--    24 hours (as the API already does for the caller), push delivery records after 30 days, and push devices
--    disabled (dropped by Apple or Google, or signed out) more than 30 days ago.

-- ---------------------------------------------------------------- 1. deletion
-- Notices are immutable except read_at (0021). The one other change allowed: deletion removing the fan's handle,
-- while scrub_deleted_user runs (transaction-local fandom.scrubbing_notices, as fandom.removing_account in 0037).
CREATE OR REPLACE FUNCTION trg_notifications_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF current_setting('fandom.scrubbing_notices', true) = 'on'
     AND (NEW.id, NEW.user_id, NEW.kind, NEW.subject_id, NEW.created_at, NEW.read_at)
         IS NOT DISTINCT FROM (OLD.id, OLD.user_id, OLD.kind, OLD.subject_id, OLD.created_at, OLD.read_at)
     AND NEW.data = OLD.data - 'handle' - 'host_handle' THEN
    RETURN NEW;
  END IF;
  IF (NEW.id, NEW.user_id, NEW.kind, NEW.subject_id, NEW.data, NEW.created_at)
     IS DISTINCT FROM (OLD.id, OLD.user_id, OLD.kind, OLD.subject_id, OLD.data, OLD.created_at)
     OR (OLD.read_at IS NOT NULL AND NEW.read_at IS DISTINCT FROM OLD.read_at) THEN
    RAISE EXCEPTION 'notification: only read_at changes, once' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;

-- What deletion removes besides 0001's users_delete and the other users_* triggers. Safe to repeat.
CREATE FUNCTION scrub_deleted_user(p_user uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM user_album_saves   WHERE user_id = p_user;
  DELETE FROM user_plays         WHERE user_id = p_user;
  DELETE FROM fan_overall_scores WHERE user_id = p_user;
  DELETE FROM plus_one_declines  WHERE guest_user_id = p_user OR host_user_id = p_user;
  DELETE FROM idempotency_keys   WHERE user_id = p_user;
  -- Already unlinked by users_avatar; deleting the row queues the file for storage.cleanup.
  DELETE FROM uploads            WHERE user_id = p_user AND purpose = 'avatar';

  -- Notices to others that name the fan (data.handle / data.host_handle). The text then reads "a deleted account".
  PERFORM set_config('fandom.scrubbing_notices', 'on', true);
  UPDATE notifications SET data = data - 'handle'
   WHERE data ? 'handle'
     AND ((kind = 'staff_review'           AND subject_id IN (SELECT id FROM submissions   WHERE user_id = p_user))
       OR (kind = 'staff_flag_before_draw' AND subject_id IN (SELECT id FROM fraud_flags   WHERE user_id = p_user))
       OR (kind = 'staff_appeal'           AND subject_id IN (SELECT id FROM fraud_appeals WHERE user_id = p_user)));
  UPDATE notifications SET data = data - 'host_handle'
   WHERE kind = 'guest_named' AND data ? 'host_handle'
     AND subject_id IN (SELECT id FROM allocation_registrations WHERE user_id = p_user);
  PERFORM set_config('fandom.scrubbing_notices', '', true);
END $$;

CREATE FUNCTION trg_user_delete_privacy() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM scrub_deleted_user(NEW.id);
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_delete_privacy AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_delete_privacy();

-- Accounts deleted before this migration: the same cleanup now, and trivia of those already purged.
SELECT scrub_deleted_user(id) FROM users WHERE deleted_at IS NOT NULL;
DELETE FROM trivia_questions t USING users u WHERE u.id = t.user_id AND u.content_purged_at IS NOT NULL;

-- ---------------------------------------------------------------- 2. purge
-- Same as 0004, plus trivia and the listening-data backstop.
CREATE OR REPLACE FUNCTION purge_deleted_user(p_user uuid, p_retention interval DEFAULT interval '30 days')
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE v_deleted timestamptz; v_purged timestamptz; v_rows integer;
BEGIN
  SELECT deleted_at, content_purged_at INTO v_deleted, v_purged FROM users WHERE id = p_user FOR UPDATE;
  IF v_deleted IS NULL THEN RAISE EXCEPTION 'user % is not deleted', p_user; END IF;
  IF v_purged IS NOT NULL THEN RETURN false; END IF;
  IF v_deleted > now() - p_retention THEN
    RAISE EXCEPTION 'user % is still within the retention window', p_user;
  END IF;
  -- Stored files must be removed from object storage (and their rows deleted) first, or they would be orphaned.
  IF EXISTS (SELECT 1 FROM submission_files f JOIN submissions s ON s.id = f.submission_id WHERE s.user_id = p_user) THEN
    RAISE EXCEPTION 'user % still has stored submission files; delete them from storage first', p_user;
  END IF;

  -- Keep the ledger but take the account out of every score.
  UPDATE score_events SET reversed_at = now() WHERE user_id = p_user AND reversed_at IS NULL;
  DELETE FROM user_artist_scores WHERE user_id = p_user;
  DELETE FROM share_cards        WHERE user_id = p_user;
  UPDATE outbound_clicks SET user_id = NULL WHERE user_id = p_user;
  DELETE FROM lists              WHERE user_id = p_user;
  DELETE FROM submissions        WHERE user_id = p_user;
  DELETE FROM trivia_questions   WHERE user_id = p_user;
  -- Deleted at deletion already; a sync or poll that was running then may have written after it.
  DELETE FROM spotify_connections WHERE user_id = p_user;
  DELETE FROM user_artist_signals WHERE user_id = p_user;
  DELETE FROM user_track_likes    WHERE user_id = p_user;
  DELETE FROM user_album_saves    WHERE user_id = p_user;
  DELETE FROM user_plays          WHERE user_id = p_user;
  DELETE FROM fan_overall_scores  WHERE user_id = p_user;

  -- Remove comments nobody replied to, repeatedly (a reply's removal can free its parent);
  -- tombstone the rest.
  LOOP
    DELETE FROM comments c WHERE c.user_id = p_user
       AND NOT EXISTS (SELECT 1 FROM comments r WHERE r.parent_id = c.id);
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    EXIT WHEN v_rows = 0;
  END LOOP;
  UPDATE comments SET body = '[deleted]', deleted_at = COALESCE(deleted_at, now()) WHERE user_id = p_user;

  -- Logs last: comments on them (including other people's) go with them.
  DELETE FROM album_logs WHERE user_id = p_user;

  UPDATE users SET content_purged_at = now() WHERE id = p_user;
  RETURN true;
END $$;

-- ---------------------------------------------------------------- 3. housekeeping retention
-- One row per kind of record removed. Safe to repeat. The periods are housekeeping, not product choices: an expired or
-- revoked session can never be used again, an idempotency key only replays within 24 hours, Expo receipts are read
-- within hours, and a disabled device is one Apple or Google no longer accepts (registering it again makes a new row).
CREATE FUNCTION prune_expired_records(p_sessions interval DEFAULT interval '30 days',
                                      p_idempotency interval DEFAULT interval '24 hours',
                                      p_push interval DEFAULT interval '30 days')
RETURNS TABLE (record text, removed integer) LANGUAGE plpgsql AS $$
DECLARE v integer;
BEGIN
  DELETE FROM auth_sessions
   WHERE expires_at < now() - p_sessions OR revoked_at < now() - p_sessions;   -- refresh tokens cascade
  GET DIAGNOSTICS v = ROW_COUNT; record := 'auth_sessions'; removed := v; RETURN NEXT;

  DELETE FROM idempotency_keys WHERE created_at < now() - p_idempotency;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'idempotency_keys'; removed := v; RETURN NEXT;

  DELETE FROM push_deliveries WHERE sent_at < now() - p_push;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'push_deliveries'; removed := v; RETURN NEXT;

  DELETE FROM push_devices WHERE disabled_at < now() - p_push;
  GET DIAGNOSTICS v = ROW_COUNT; record := 'push_devices'; removed := v; RETURN NEXT;
END $$;

CREATE INDEX auth_sessions_expires_idx ON auth_sessions (expires_at);
CREATE INDEX push_deliveries_sent_idx ON push_deliveries (sent_at);
