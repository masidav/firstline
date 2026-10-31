-- Profile pictures (2026-10-31). A fan uploads a photo through the upload flow (purpose 'avatar', JPEG or PNG up to
-- 15 MB, like proof photos); the API crops it square, scales it to at most 512 px, and re-encodes it as a JPEG (which
-- also drops its metadata) under media/avatars/. users.avatar_upload_id names that upload and users.avatar_url is its
-- public link (GET /v1/avatars/{upload id}: no sign-in, works only while the upload is the fan's current picture).
-- The link goes with the upload: whenever avatar_upload_id is cleared (removed by the fan, the upload row deleted,
-- the account deleted) avatar_url is cleared too, so nobody is left pointing at a picture that is gone.

ALTER TABLE uploads DROP CONSTRAINT uploads_purpose_kind_check;
ALTER TABLE uploads ADD CONSTRAINT uploads_purpose_kind_check CHECK (
  (purpose IN ('submission', 'avatar') AND mime_type IN ('image/jpeg', 'image/png') AND declared_bytes BETWEEN 1 AND 15728640)
  OR (purpose = 'featured_video' AND mime_type = 'video/mp4' AND declared_bytes BETWEEN 1 AND 104857600));

ALTER TABLE users
  ADD COLUMN avatar_upload_id uuid REFERENCES uploads ON DELETE SET NULL,
  ADD CONSTRAINT users_avatar_link CHECK ((avatar_upload_id IS NULL) = (avatar_url IS NULL));
-- One fan per picture; also how storage.cleanup tells an avatar slot from an expired one.
CREATE UNIQUE INDEX users_avatar_upload_idx ON users (avatar_upload_id) WHERE avatar_upload_id IS NOT NULL;

-- A picture must be a finished avatar upload of the same fan. The link is cleared with the upload (the FK's SET NULL
-- goes through here too). Deletion clears both, and they stay empty (same rule as trg_user_delete in 0001).
CREATE FUNCTION trg_user_avatar() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.deleted_at IS NOT NULL AND NEW.avatar_upload_id IS NOT NULL THEN
    RAISE EXCEPTION 'user % is deleted; personal fields stay empty', OLD.id;
  END IF;
  IF NEW.deleted_at IS NOT NULL THEN
    NEW.avatar_upload_id := NULL;
  ELSIF NEW.avatar_upload_id IS NOT NULL AND NEW.avatar_upload_id IS DISTINCT FROM OLD.avatar_upload_id AND NOT EXISTS (
      SELECT 1 FROM uploads WHERE id = NEW.avatar_upload_id AND user_id = NEW.id AND purpose = 'avatar' AND uploaded_at IS NOT NULL) THEN
    RAISE EXCEPTION 'avatar: the picture must be a finished avatar upload of this user' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.avatar_upload_id IS NULL THEN
    NEW.avatar_url := NULL;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER users_avatar BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION trg_user_avatar();
