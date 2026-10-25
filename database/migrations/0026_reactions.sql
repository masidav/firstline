-- Emoji reactions instead of comments (decided 2026-10-25): fans react to feed posts (shared photos, album logs,
-- prompt answers) with a fixed set of emoji, several different ones per post if they like, and can take a reaction
-- back. No text, so nothing to moderate beyond the post itself, and no points, so nothing to farm. Custom emoji (from
-- Shelby) are a later addition: reactions are stored by key, so new keys can map to images without a data change.

CREATE TABLE post_reactions (
  target_type text NOT NULL CHECK (target_type IN ('photo', 'log', 'prompt_answer')),
  target_id   uuid NOT NULL,                     -- submissions.id, album_logs.id, or prompt_answers.id
  user_id     uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  reaction    text NOT NULL CONSTRAINT post_reactions_known CHECK (reaction IN ('fire', 'heart', 'laugh', 'rock', 'wow', 'clap')),
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (target_type, target_id, user_id, reaction)
);
CREATE INDEX post_reactions_user_idx ON post_reactions (user_id);

-- A reaction points at a post that exists (no foreign key can span three tables).
CREATE FUNCTION trg_reaction_target_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_exists boolean;
BEGIN
  v_exists := CASE NEW.target_type
                WHEN 'photo' THEN EXISTS (SELECT 1 FROM submissions WHERE id = NEW.target_id)
                WHEN 'log' THEN EXISTS (SELECT 1 FROM album_logs WHERE id = NEW.target_id)
                ELSE EXISTS (SELECT 1 FROM prompt_answers WHERE id = NEW.target_id)
              END;
  IF NOT v_exists THEN
    RAISE EXCEPTION 'reaction: no % with this id', NEW.target_type USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_reaction_target_check BEFORE INSERT ON post_reactions
  FOR EACH ROW EXECUTE FUNCTION trg_reaction_target_check();

-- A post that goes away takes its reactions with it.
CREATE FUNCTION trg_post_reactions_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM post_reactions WHERE target_type = TG_ARGV[0] AND target_id = OLD.id;
  RETURN NULL;
END $$;
CREATE TRIGGER submissions_reactions_cleanup AFTER DELETE ON submissions
  FOR EACH ROW EXECUTE FUNCTION trg_post_reactions_cleanup('photo');
CREATE TRIGGER album_logs_reactions_cleanup AFTER DELETE ON album_logs
  FOR EACH ROW EXECUTE FUNCTION trg_post_reactions_cleanup('log');
CREATE TRIGGER prompt_answers_reactions_cleanup AFTER DELETE ON prompt_answers
  FOR EACH ROW EXECUTE FUNCTION trg_post_reactions_cleanup('prompt_answer');

-- A deleted account's reactions go at once (they are light, personal signals, not content kept for 30 days).
CREATE FUNCTION trg_user_reactions_scrub() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM post_reactions WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_reactions_scrub AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_reactions_scrub();
