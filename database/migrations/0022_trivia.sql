-- Daily music trivia (decided 2026-10-25): one question a day per fan, about one of the fan's artists, made from the
-- stored catalog (which album a song is on, or the year an album came out). A right answer earns ledger points worth
-- a tenth of logging an album (album_logged is 3.00, so 0.30), at most once a day; like every ledger award it only
-- counts for active artists and goes through award_points().

INSERT INTO scoring_rules (rule_key, points, daily_cap_count, description)
VALUES ('trivia_correct', 0.30, 1, 'Daily music trivia answered right: one tenth of album_logged (decided 2026-10-25)');

-- The ledger learns a new source type.
ALTER TABLE score_events DROP CONSTRAINT score_events_source_type_check;
ALTER TABLE score_events ADD CONSTRAINT score_events_source_type_check
  CHECK (source_type IN ('album_log', 'comment', 'submission', 'trivia'));

CREATE TABLE trivia_questions (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  day           date NOT NULL,                                          -- the UTC day it is for
  artist_id     uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  kind          text NOT NULL CHECK (kind IN ('track_album', 'release_year')),
  prompt        text NOT NULL CHECK (char_length(prompt) BETWEEN 1 AND 500),
  options       text[] NOT NULL CHECK (cardinality(options) BETWEEN 2 AND 4),
  answer_index  smallint NOT NULL,
  chosen_index  smallint,
  correct       boolean,
  answered_at   timestamptz,
  created_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
  CONSTRAINT trivia_questions_one_a_day UNIQUE (user_id, day),
  CONSTRAINT trivia_questions_answer_in_options CHECK (answer_index >= 0 AND answer_index < cardinality(options)),
  CONSTRAINT trivia_questions_chosen_in_options CHECK (chosen_index IS NULL OR (chosen_index >= 0 AND chosen_index < cardinality(options))),
  CONSTRAINT trivia_questions_answered CHECK ((answered_at IS NULL) = (chosen_index IS NULL) AND (answered_at IS NULL) = (correct IS NULL))
);

-- The question never changes; it is answered once, through answer_trivia() (fandom.answering = its id).
CREATE FUNCTION trg_trivia_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.id, NEW.user_id, NEW.day, NEW.artist_id, NEW.kind, NEW.prompt, NEW.options, NEW.answer_index, NEW.created_at)
     IS DISTINCT FROM (OLD.id, OLD.user_id, OLD.day, OLD.artist_id, OLD.kind, OLD.prompt, OLD.options, OLD.answer_index, OLD.created_at) THEN
    RAISE EXCEPTION 'trivia: a question never changes' USING ERRCODE = 'P0001';
  END IF;
  IF OLD.answered_at IS NOT NULL OR current_setting('fandom.answering', true) IS DISTINCT FROM NEW.id::text THEN
    RAISE EXCEPTION 'trivia: answered once, through answer_trivia()' USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_trivia_guard BEFORE UPDATE ON trivia_questions
  FOR EACH ROW EXECUTE FUNCTION trg_trivia_guard();

-- Answers today's question for its fan. Returns whether it was right. Errors: 'trivia blocked: not_found',
-- 'answered', or 'expired' (another day's question).
CREATE FUNCTION answer_trivia(p_user uuid, p_question uuid, p_choice integer) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE q trivia_questions; v_correct boolean;
BEGIN
  SELECT * INTO q FROM trivia_questions WHERE id = p_question FOR UPDATE;
  IF NOT FOUND OR q.user_id <> p_user THEN RAISE EXCEPTION 'trivia blocked: not_found' USING ERRCODE = 'P0001'; END IF;
  IF q.answered_at IS NOT NULL THEN RAISE EXCEPTION 'trivia blocked: answered' USING ERRCODE = 'P0001'; END IF;
  IF q.day <> (now() AT TIME ZONE 'UTC')::date THEN RAISE EXCEPTION 'trivia blocked: expired' USING ERRCODE = 'P0001'; END IF;
  IF p_choice < 0 OR p_choice >= cardinality(q.options) THEN RAISE EXCEPTION 'trivia blocked: choice' USING ERRCODE = 'P0001'; END IF;
  v_correct := p_choice = q.answer_index;
  PERFORM set_config('fandom.answering', p_question::text, true);
  UPDATE trivia_questions SET chosen_index = p_choice, correct = v_correct, answered_at = clock_timestamp() WHERE id = p_question;
  PERFORM set_config('fandom.answering', '', true);
  IF v_correct THEN
    PERFORM award_points(p_user, q.artist_id, 'trivia_correct', 'trivia', p_question);
  END IF;
  RETURN v_correct;
END $$;
