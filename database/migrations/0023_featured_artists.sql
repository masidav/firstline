-- Featured artist of the week (decided 2026-10-25): staff pick one tracked artist per week (weeks start on Monday,
-- UTC) with a video (an uploaded MP4, or a YouTube or TikTok link) and a song on Spotify. It shows at the top of every
-- fan's home feed for that week.

-- Uploads learn a second purpose: a staff-uploaded MP4 for a featured artist (up to 100 MB, stored as uploaded: the
-- photo metadata stripping doesn't apply to video). Photos keep their 15 MB JPEG/PNG rule.
ALTER TABLE uploads DROP CONSTRAINT uploads_purpose_check;
ALTER TABLE uploads DROP CONSTRAINT uploads_mime_type_check;
ALTER TABLE uploads DROP CONSTRAINT uploads_declared_bytes_check;
ALTER TABLE uploads ADD CONSTRAINT uploads_purpose_kind_check CHECK (
  (purpose = 'submission' AND mime_type IN ('image/jpeg', 'image/png') AND declared_bytes BETWEEN 1 AND 15728640)
  OR (purpose = 'featured_video' AND mime_type = 'video/mp4' AND declared_bytes BETWEEN 1 AND 104857600));

CREATE TABLE featured_artists (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  week_start        date NOT NULL CONSTRAINT featured_artists_one_a_week UNIQUE
                      CONSTRAINT featured_artists_monday CHECK (extract(isodow FROM week_start) = 1),
  artist_id         uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  headline          text CHECK (char_length(headline) BETWEEN 1 AND 200),
  video_kind        text NOT NULL CHECK (video_kind IN ('upload', 'youtube', 'tiktok')),
  video_url         text CHECK (video_url ~ '^https://'),                -- the YouTube or TikTok link
  video_upload_id   uuid REFERENCES uploads ON DELETE RESTRICT,           -- the uploaded MP4
  spotify_track_id  text NOT NULL CHECK (spotify_track_id ~ '^[0-9A-Za-z]{22}$'),
  track_title       text NOT NULL CHECK (track_title <> ''),
  track_album_title text,
  track_cover_url   text,
  created_by        uuid REFERENCES users ON DELETE SET NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT featured_artists_video CHECK (
    (video_kind = 'upload') = (video_upload_id IS NOT NULL) AND (video_kind = 'upload') = (video_url IS NULL))
);

-- An uploaded video must be a finished featured_video upload.
CREATE FUNCTION trg_featured_video_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.video_upload_id IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM uploads WHERE id = NEW.video_upload_id AND purpose = 'featured_video' AND uploaded_at IS NOT NULL) THEN
    RAISE EXCEPTION 'featured: the video must be a finished featured_video upload' USING ERRCODE = 'P0001';
  END IF;
  NEW.updated_at := now();
  RETURN NEW;
END $$;
CREATE TRIGGER trg_featured_video_check BEFORE INSERT OR UPDATE ON featured_artists
  FOR EACH ROW EXECUTE FUNCTION trg_featured_video_check();
