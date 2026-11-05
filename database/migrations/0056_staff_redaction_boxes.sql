-- Staff-drawn redaction boxes (Phase 7, 2026-11-05; services/api/src/features/redaction, workers/image-redaction).
--
-- When the redaction worker sends a file to staff (needs_review) and they archive it, they can now draw boxes over
-- what the scanner missed: a printed order number, a seat strip, anything they don't want archived. The boxes
-- travel with the archive decision, are stored here, and the worker paints them black before it scans for codes and
-- archives the copy. Nothing unredacted is archived, as before: the boxes are painted on the copy, the original stays
-- in quarantine until the archive write succeeds and is then queued for deletion (0027).
--
-- A box is {"x", "y", "w", "h"} in fractions of the image (0..1 from the top-left corner), so it means the same thing
-- whatever size the image was shown at. At most 20 per file. The column keeps the boxes after archiving: how often
-- staff draw them (and what the note says) is how we learn whether printed order numbers are common enough to be
-- worth OCR (PROJECT_TODO.md, Phase 7).

CREATE FUNCTION redaction_boxes_valid(p_boxes jsonb) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
  SELECT jsonb_typeof(p_boxes) = 'array'
     AND jsonb_array_length(p_boxes) BETWEEN 1 AND 20
     AND NOT EXISTS (
       SELECT 1 FROM jsonb_array_elements(p_boxes) AS b
       WHERE jsonb_typeof(b) <> 'object'
          -- IS DISTINCT FROM: jsonb_typeof of a missing key is null, and null <> 'number' is not true.
          OR jsonb_typeof(b->'x') IS DISTINCT FROM 'number' OR jsonb_typeof(b->'y') IS DISTINCT FROM 'number'
          OR jsonb_typeof(b->'w') IS DISTINCT FROM 'number' OR jsonb_typeof(b->'h') IS DISTINCT FROM 'number'
          OR (b->>'x')::numeric < 0 OR (b->>'y')::numeric < 0
          OR (b->>'w')::numeric <= 0 OR (b->>'h')::numeric <= 0
          OR (b->>'x')::numeric + (b->>'w')::numeric > 1
          OR (b->>'y')::numeric + (b->>'h')::numeric > 1);
$$;

ALTER TABLE submission_files
  ADD COLUMN staff_boxes jsonb
    CONSTRAINT submission_files_staff_boxes_check CHECK (staff_boxes IS NULL OR redaction_boxes_valid(staff_boxes));

COMMENT ON COLUMN submission_files.staff_boxes IS
  'Boxes staff drew when archiving a file the scanner could not settle, as fractions of the image; painted black by the worker';
