-- Weekly trivia stays open until it's answered (decided 2026-10-27): a fan's question doesn't expire when its week
-- ends. The fan sees it, for its artist, until they answer it; then its results until the next Tuesday's question.
-- Points are unchanged (trivia_correct, 0.30). Same as 0034's answer_trivia() without the 'expired' check.

CREATE OR REPLACE FUNCTION answer_trivia(p_user uuid, p_question uuid, p_choice integer) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE q trivia_questions; v_correct boolean;
BEGIN
  SELECT * INTO q FROM trivia_questions WHERE id = p_question FOR UPDATE;
  IF NOT FOUND OR q.user_id <> p_user THEN RAISE EXCEPTION 'trivia blocked: not_found' USING ERRCODE = 'P0001'; END IF;
  IF q.answered_at IS NOT NULL THEN RAISE EXCEPTION 'trivia blocked: answered' USING ERRCODE = 'P0001'; END IF;
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

-- The fan's current question: their unanswered weekly one if any, else the one they answered since this week's Tuesday
-- (its results show until the next question). Null means it's time for this week's question. Questions left
-- unanswered from the daily days before 0034 (not on a Tuesday) stay behind.
CREATE FUNCTION current_trivia_question(p_user uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
  SELECT id FROM trivia_questions
   WHERE user_id = p_user
     AND ((answered_at IS NULL AND day = trivia_week_day(day))
          OR answered_at >= (trivia_week_day()::timestamp AT TIME ZONE 'UTC'))
   ORDER BY answered_at IS NULL DESC, day DESC, created_at DESC
   LIMIT 1;
$$;
