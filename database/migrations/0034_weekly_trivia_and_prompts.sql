-- Trivia and prompts go weekly (requested 2026-10-26): trivia comes out on Tuesdays, prompts on Thursdays (UTC), so a
-- day has at most one of them. Each stays open for its whole week, until the next one comes out (provisional, listed in
-- PROJECT_TODO.md). The tables keep their day columns, which now hold the day a question came out; "today's" question
-- is the one for the latest Tuesday or Thursday.

-- The latest Tuesday (trivia) or Thursday (prompts) on or before p_on.
CREATE FUNCTION trivia_week_day(p_on date DEFAULT (now() AT TIME ZONE 'UTC')::date) RETURNS date
LANGUAGE sql STABLE AS $$
  SELECT p_on - ((extract(isodow FROM p_on)::int - 2 + 7) % 7);
$$;
CREATE FUNCTION prompt_week_day(p_on date DEFAULT (now() AT TIME ZONE 'UTC')::date) RETURNS date
LANGUAGE sql STABLE AS $$
  SELECT p_on - ((extract(isodow FROM p_on)::int - 4 + 7) % 7);
$$;

-- Same as 0022, with "this week's" in place of "today's".
CREATE OR REPLACE FUNCTION answer_trivia(p_user uuid, p_question uuid, p_choice integer) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE q trivia_questions; v_correct boolean;
BEGIN
  SELECT * INTO q FROM trivia_questions WHERE id = p_question FOR UPDATE;
  IF NOT FOUND OR q.user_id <> p_user THEN RAISE EXCEPTION 'trivia blocked: not_found' USING ERRCODE = 'P0001'; END IF;
  IF q.answered_at IS NOT NULL THEN RAISE EXCEPTION 'trivia blocked: answered' USING ERRCODE = 'P0001'; END IF;
  IF q.day <> trivia_week_day() THEN RAISE EXCEPTION 'trivia blocked: expired' USING ERRCODE = 'P0001'; END IF;
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

-- Same as 0025, with "the prompt's week" in place of "the prompt's day".
CREATE OR REPLACE FUNCTION trg_prompt_answer_guard() RETURNS trigger LANGUAGE plpgsql AS $$
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
    IF v_day <> prompt_week_day() THEN
      RAISE EXCEPTION 'prompt: answers can be written or changed only in the prompt''s week' USING ERRCODE = 'P0001';
    END IF;
    NEW.updated_at := now();
  END IF;
  RETURN NEW;
END $$;
