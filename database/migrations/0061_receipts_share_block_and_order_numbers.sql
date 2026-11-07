-- Proof of fandom, three follow-ups (Phase 7, 2026-11-06):
--
-- 1. Receipts out of the scroll feed (requested 2026-10-26). Both options from PROJECT_TODO, provisionally: a new proof
--    kind 'receipt' (an order confirmation or receipt; proof like a ticket, never shareable, like tickets and
--    screenshots; points as merch, 20, provisional), and a staff "not shareable" mark on any submission
--    (share_blocked_at): a merch photo that turns out to be a receipt, or shows personal details, comes off the feed
--    and can't go back on. The share guard refuses a blocked submission; blocking clears shared_at.
-- 2. Printed order numbers on tickets, screenshots, and receipts (workers/image-redaction, OCR when a Tesseract command
--    is configured): submission_files.text_redacted counts the numbers blacked out; submissions.extracted gets counts
--    only (never the numbers); submissions.order_ref_hash gets the SHA-256 of the first normalized order number, so
--    the one-order-one-fan rule (submissions_order_ref_uidx, 0001) finally has data. A match with another verified
--    submission sends the file to staff instead of archiving it.
-- 3. The same photo from two fans: the API writes a file_hash check for staff when a new submission's file matches
--    another fan's (same bytes), instead of the match being invisible in review.
--
-- The enum value is added first and only ever compared as text below (a value can't be used in the transaction that
-- adds it).
ALTER TYPE submission_kind ADD VALUE IF NOT EXISTS 'receipt';

INSERT INTO scoring_rules (rule_key, points, description) VALUES
  ('receipt_verified', 20.00, 'Verified receipt or order confirmation (provisional points, 2026-11-06: same as merch)');

ALTER TABLE submissions
  ADD COLUMN share_blocked_at   timestamptz,                                    -- staff: never on the feed
  ADD COLUMN share_blocked_by   uuid REFERENCES users ON DELETE SET NULL,
  ADD COLUMN share_blocked_note text CHECK (char_length(share_blocked_note) <= 500),
  ADD CONSTRAINT submissions_share_block_fields CHECK (share_blocked_at IS NOT NULL OR (share_blocked_by IS NULL AND share_blocked_note IS NULL));

-- Blocking takes the photo off the feed (runs before the share guard: BEFORE triggers fire in name order).
CREATE FUNCTION trg_submission_share_block() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.shared_at := NULL;
  RETURN NEW;
END $$;
CREATE TRIGGER submissions_share_block BEFORE UPDATE OF share_blocked_at ON submissions
  FOR EACH ROW WHEN (NEW.share_blocked_at IS NOT NULL AND OLD.share_blocked_at IS NULL)
  EXECUTE FUNCTION trg_submission_share_block();

-- Same as 0012, plus: a blocked submission can't be shared. Receipts fall under the kind rule (not merch or concert).
CREATE OR REPLACE FUNCTION trg_submission_share_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.shared_at IS NOT NULL THEN
    IF NEW.kind::text NOT IN ('merch', 'concert_photo') THEN
      RAISE EXCEPTION 'only merch and concert photos can be shared';
    END IF;
    IF NEW.status <> 'verified' THEN
      RAISE EXCEPTION 'only verified submissions can be shared';
    END IF;
    IF NEW.share_blocked_at IS NOT NULL THEN
      RAISE EXCEPTION 'staff marked this submission as not shareable';
    END IF;
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER submissions_share_guard ON submissions;
CREATE TRIGGER submissions_share_guard BEFORE INSERT OR UPDATE OF shared_at, status, kind, share_blocked_at ON submissions
  FOR EACH ROW WHEN (NEW.shared_at IS NOT NULL)
  EXECUTE FUNCTION trg_submission_share_guard();

-- Printed order numbers blacked out by OCR (null: OCR didn't run on this file).
ALTER TABLE submission_files ADD COLUMN text_redacted integer CHECK (text_redacted >= 0);
