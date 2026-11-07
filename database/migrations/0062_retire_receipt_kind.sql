-- Receipts decision (owner, 2026-11-07): keep only the staff "not shareable" mark from 0061 and retire the `receipt`
-- proof kind that was built beside it. A receipt a fan sends as proof is reviewed like any other photo, and one that
-- reaches the feed as "merch" is handled by the mark (share_blocked_at). Nothing about the mark changes here.
--
-- Postgres can't drop an enum value, so 'receipt' stays in submission_kind and a CHECK refuses it on insert and update.
-- The constraint is validated, so the migration stops if a receipt row exists: none is expected (the kind shipped on
-- 2026-11-06, beta was switched off the same day, and the demo seed writes no receipts). If one does exist, decide
-- what it becomes (a ticket, or withdrawn) in a follow-up migration rather than guessing here.
DO $$
DECLARE n integer;
BEGIN
  SELECT count(*) INTO n FROM submissions WHERE kind::text = 'receipt';
  IF n > 0 THEN
    RAISE EXCEPTION '0062: % submission(s) of kind receipt exist; decide what they become before retiring the kind', n;
  END IF;
END $$;

ALTER TABLE submissions ADD CONSTRAINT submissions_kind_not_receipt CHECK (kind::text <> 'receipt');

-- award_points skips inactive rules; past awards stay in the ledger (append-only).
UPDATE scoring_rules
   SET active = false,
       description = description || '. Retired 2026-11-07: the kind is no longer accepted (0062).'
 WHERE rule_key = 'receipt_verified';
