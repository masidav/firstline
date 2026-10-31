-- Drop one built-in prompt (requested 2026-10-31): "Which album would you hand someone to get them into your
-- favourite artist?". prompt_for_day() picks by row order and the live count, so the gap in position is harmless.
-- A week that already got this prompt keeps it if anyone answered (answers reference their prompt); an unanswered
-- one is dropped so the week gets the next library prompt instead.
DELETE FROM prompt_library
 WHERE text = 'Which album would you hand someone to get them into your favourite artist?';

DELETE FROM prompts p
 WHERE p.source = 'library'
   AND p.text = 'Which album would you hand someone to get them into your favourite artist?'
   AND NOT EXISTS (SELECT 1 FROM prompt_answers a WHERE a.prompt_id = p.id);
