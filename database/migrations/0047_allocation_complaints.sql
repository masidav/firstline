-- Scalping complaints for the pilot case study (Phase 11, 2026-10-30). The show report (sell-through, returns,
-- re-offers, empty seats) is computed from registrations and reservations; nothing in the platform records resale
-- complaints, because tickets never leave the account they were drawn for. Complaints reach the promoter or staff
-- from outside (fans, the venue, the ticketer), so they enter them here by hand: one row per allocation with a
-- count and a note. Never invented: an allocation without a row reads as "not recorded", not as zero.
CREATE TABLE allocation_complaints (
  allocation_id       uuid PRIMARY KEY REFERENCES allocations ON DELETE RESTRICT,
  scalping_complaints integer NOT NULL CONSTRAINT allocation_complaints_count_check CHECK (scalping_complaints >= 0),
  note                text CONSTRAINT allocation_complaints_note_check CHECK (note IS NULL OR length(btrim(note)) BETWEEN 1 AND 2000),
  updated_by          uuid NOT NULL REFERENCES users ON DELETE RESTRICT,   -- a promoter admin or a staff member
  updated_at          timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- The database stamps the time of every change.
CREATE FUNCTION trg_allocation_complaints_stamp() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' AND NEW.allocation_id IS DISTINCT FROM OLD.allocation_id THEN
    RAISE EXCEPTION 'complaints: the record belongs to its allocation for good' USING ERRCODE = 'P0001';
  END IF;
  NEW.updated_at := clock_timestamp();
  RETURN NEW;
END $$;
CREATE TRIGGER trg_allocation_complaints_stamp BEFORE INSERT OR UPDATE ON allocation_complaints
  FOR EACH ROW EXECUTE FUNCTION trg_allocation_complaints_stamp();
