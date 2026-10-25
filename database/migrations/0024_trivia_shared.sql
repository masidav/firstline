-- Shared daily trivia (decided 2026-10-25): once a fan answers, the card names one or two people they follow who got
-- it right and the percent of fans who did. For that, fans of the same artist get the same question on the same day:
-- one shared question per artist per day (trivia_daily_questions), made from the catalog by the first fan who asks.
-- Each fan's row in trivia_questions (0022) is their copy and holds their answer; daily_question_id links the copies.

CREATE TABLE trivia_daily_questions (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  day          date NOT NULL,
  artist_id    uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  kind         text NOT NULL CHECK (kind IN ('track_album', 'release_year')),
  prompt       text NOT NULL CHECK (char_length(prompt) BETWEEN 1 AND 500),
  options      text[] NOT NULL CHECK (cardinality(options) BETWEEN 2 AND 4),
  answer_index smallint NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
  CONSTRAINT trivia_daily_one_per_artist UNIQUE (day, artist_id),
  CONSTRAINT trivia_daily_answer_in_options CHECK (answer_index >= 0 AND answer_index < cardinality(options))
);

-- A shared question never changes once fans may have seen it (deleting an artist still cascades).
CREATE FUNCTION trg_trivia_daily_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' AND pg_trigger_depth() > 1 THEN RETURN OLD; END IF;
  RAISE EXCEPTION 'trivia: a shared daily question never changes' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_trivia_daily_guard BEFORE UPDATE OR DELETE ON trivia_daily_questions
  FOR EACH ROW EXECUTE FUNCTION trg_trivia_daily_guard();

ALTER TABLE trivia_questions ADD COLUMN daily_question_id uuid REFERENCES trivia_daily_questions ON DELETE CASCADE;
CREATE INDEX trivia_questions_daily_idx ON trivia_questions (daily_question_id) WHERE answered_at IS NOT NULL;

-- A fan's copy is the shared question exactly: same day, artist, wording, options, and answer.
CREATE FUNCTION trg_trivia_copy_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.daily_question_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM trivia_daily_questions d
        WHERE d.id = NEW.daily_question_id AND d.day = NEW.day AND d.artist_id = NEW.artist_id AND d.kind = NEW.kind
          AND d.prompt = NEW.prompt AND d.options = NEW.options AND d.answer_index = NEW.answer_index) THEN
    RAISE EXCEPTION 'trivia: a fan''s question must match its shared daily question' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_trivia_copy_check BEFORE INSERT ON trivia_questions
  FOR EACH ROW EXECUTE FUNCTION trg_trivia_copy_check();
