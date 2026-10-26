-- Music prompts (requested 2026-10-25): one open question a day for every fan, for example "What's your best
-- hypothetical band name?". Fans answer in a line or two; answers show on the home feed like album logs (everyone's
-- public ones, followers-only ones to followers), can be reported, and staff can take them down. No points: answers
-- are opinions, and points would invite spam.
-- Staff can schedule a day's prompt; a day without one gets the next prompt from a built-in library, so there is always
-- a prompt. One answer per fan per prompt, editable on its day.

CREATE TABLE prompt_library (
  position integer PRIMARY KEY CHECK (position > 0),
  text     text NOT NULL UNIQUE CHECK (char_length(text) BETWEEN 1 AND 200)
);
INSERT INTO prompt_library (position, text) VALUES
  (1, 'What''s your best hypothetical band name?'),
  (2, 'Which album would you hand someone to get them into your favourite artist?'),
  (3, 'The song you''d walk out to in a stadium?'),
  (4, 'The first concert you ever went to?'),
  (5, 'One song you''ll never skip, no matter what?'),
  (6, 'Name an album that''s better than its reputation.'),
  (7, 'The lyric you''d get tattooed?'),
  (8, 'Two artists who should make an album together?'),
  (9, 'The best opening track of any album?'),
  (10, 'A song that sounds like your hometown?'),
  (11, 'Which show would you go back in time to see?'),
  (12, 'The album you played most this year?'),
  (13, 'Your go-to karaoke song?'),
  (14, 'Name a deep cut more people should hear.'),
  (15, 'A cover that beats the original?'),
  (16, 'The best closing track of any album?'),
  (17, 'Which artist would you want to sit next to on a long flight?'),
  (18, 'Describe your music taste in three words.'),
  (19, 'The song that got you through a hard time?'),
  (20, 'Your dream festival headliner, alive or not?'),
  (21, 'An album you loved at 15 and still love?'),
  (22, 'The best album cover of all time?'),
  (23, 'What would your band''s debut album be called?'),
  (24, 'The song you''d want played at your wedding, or your funeral?'),
  (25, 'An artist you got into because of a friend?'),
  (26, 'The most underrated live band you''ve seen?'),
  (27, 'A song you''d put on a road-trip playlist first?'),
  (28, 'Which album would you save from a fire?'),
  (29, 'The guilty pleasure you''re not actually guilty about?'),
  (30, 'If your week had a soundtrack, what''s the lead single?');

CREATE TABLE prompts (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  day        date NOT NULL CONSTRAINT prompts_one_a_day UNIQUE,
  text       text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 200),
  source     text NOT NULL CHECK (source IN ('staff', 'library')),
  created_by uuid REFERENCES users ON DELETE SET NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE prompt_answers (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  prompt_id  uuid NOT NULL REFERENCES prompts ON DELETE RESTRICT,
  user_id    uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  body       text NOT NULL CHECK (char_length(btrim(body)) BETWEEN 1 AND 280),
  visibility visibility NOT NULL DEFAULT 'public' CONSTRAINT prompt_answers_shared CHECK (visibility <> 'private'),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz,                                       -- withdrawn by its author
  removed_at timestamptz,                                       -- taken down by staff after a report
  CONSTRAINT prompt_answers_one_per_fan UNIQUE (prompt_id, user_id)
);
CREATE INDEX prompt_answers_prompt_idx ON prompt_answers (prompt_id, created_at DESC) WHERE deleted_at IS NULL AND removed_at IS NULL;
CREATE INDEX prompt_answers_recent_idx ON prompt_answers (created_at DESC) WHERE deleted_at IS NULL AND removed_at IS NULL;

-- Answers change only on their prompt's day (UTC); a takedown is permanent.
CREATE FUNCTION trg_prompt_answer_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_day date;
BEGIN
  IF TG_OP = 'UPDATE' AND OLD.removed_at IS NOT NULL AND NEW.removed_at IS DISTINCT FROM OLD.removed_at THEN
    RAISE EXCEPTION 'prompt: a taken-down answer stays down' USING ERRCODE = 'P0001';
  END IF;
  IF TG_OP = 'UPDATE' AND (NEW.prompt_id, NEW.user_id, NEW.created_at) IS DISTINCT FROM (OLD.prompt_id, OLD.user_id, OLD.created_at) THEN
    RAISE EXCEPTION 'prompt: an answer stays with its prompt and author' USING ERRCODE = 'P0001';
  END IF;
  IF TG_OP = 'INSERT' OR (NEW.body, NEW.visibility, NEW.deleted_at) IS DISTINCT FROM (OLD.body, OLD.visibility, OLD.deleted_at) THEN
    SELECT day INTO v_day FROM prompts WHERE id = NEW.prompt_id;
    IF v_day <> (now() AT TIME ZONE 'UTC')::date THEN
      RAISE EXCEPTION 'prompt: answers can be written or changed only on the prompt''s day' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := now();
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_prompt_answer_guard BEFORE INSERT OR UPDATE ON prompt_answers
  FOR EACH ROW EXECUTE FUNCTION trg_prompt_answer_guard();

-- A prompt fans have answered keeps its wording.
CREATE FUNCTION trg_prompt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.text, NEW.day) IS DISTINCT FROM (OLD.text, OLD.day) AND EXISTS (SELECT 1 FROM prompt_answers WHERE prompt_id = OLD.id) THEN
    RAISE EXCEPTION 'prompt: fans have answered it, so it can''t change' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_prompt_guard BEFORE UPDATE ON prompts
  FOR EACH ROW EXECUTE FUNCTION trg_prompt_guard();

-- Today's prompt (UTC): the staff one, else the library's next one, written down the first time it is asked for so
-- everyone answers the same question all day.
CREATE FUNCTION prompt_for_day(p_day date) RETURNS prompts LANGUAGE plpgsql AS $$
DECLARE p prompts; v_count integer;
BEGIN
  SELECT * INTO p FROM prompts WHERE day = p_day;
  IF FOUND THEN RETURN p; END IF;
  SELECT count(*) INTO v_count FROM prompt_library;
  INSERT INTO prompts (day, text, source)
  SELECT p_day, l.text, 'library' FROM prompt_library l
   ORDER BY l.position OFFSET ((p_day - date '2026-01-01') % v_count + v_count) % v_count LIMIT 1
  ON CONFLICT ON CONSTRAINT prompts_one_a_day DO NOTHING;
  SELECT * INTO p FROM prompts WHERE day = p_day;
  RETURN p;
END $$;

-- ---------------------------------------------------------------- reports and takedowns (0014)
ALTER TABLE reports DROP CONSTRAINT reports_target_type_check;
ALTER TABLE reports ADD CONSTRAINT reports_target_type_check CHECK (target_type IN ('photo', 'log', 'prompt_answer'));
ALTER TABLE moderation_actions DROP CONSTRAINT moderation_actions_target_type_check;
ALTER TABLE moderation_actions ADD CONSTRAINT moderation_actions_target_type_check CHECK (target_type IN ('photo', 'log', 'prompt_answer'));

-- ---------------------------------------------------------------- account purge
-- Answers are content: kept for 30 days after deletion like logs (hidden at once, since the feed skips deleted
-- accounts), then removed when purge_deleted_user() stamps content_purged_at.
CREATE FUNCTION trg_user_prompt_answers_purge() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM prompt_answers WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_prompt_answers_purge AFTER UPDATE OF content_purged_at ON users
  FOR EACH ROW WHEN (OLD.content_purged_at IS NULL AND NEW.content_purged_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_prompt_answers_purge();
