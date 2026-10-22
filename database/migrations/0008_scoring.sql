-- Fandom score from Spotify signals (Phase 5). Each Spotify signal earns 0..1 with diminishing returns, and the
-- signals are combined by their weights in scoring_rules into a 0..100 Spotify score. Ledger points (journal,
-- proof of fandom) are added on top. Weights are relative shares: they need not add up to anything, and changing
-- a Spotify rule queues a rescore of every active artist (trg_scoring_rules_rescore_*).

ALTER TABLE scoring_rules
  -- Spotify rules: the count at which the signal earns full marks. Null for ledger awards.
  ADD COLUMN saturation numeric(8,2) CHECK (saturation > 0);

-- Even split across the five signals (decided 2026-10-21; weights expected to change often).
UPDATE scoring_rules SET points = 1, saturation = 100,
       description = 'Weight of liked tracks by the artist (full marks at saturation tracks, log curve)'
 WHERE rule_key = 'spotify_liked_track';
UPDATE scoring_rules SET points = 1, saturation = 100,
       description = 'Weight of distinct tracks by the artist in own/collaborative playlists (full marks at saturation, log curve)'
 WHERE rule_key = 'spotify_playlist_track';
INSERT INTO scoring_rules (rule_key, points, saturation, description) VALUES
  ('spotify_album_depth', 1, 10,
   'Weight of catalog depth: distinct releases the fan has tracks from, out of the artist''s catalog or saturation, whichever is smaller (linear)'),
  ('spotify_top_artist', 1, 50,
   'Weight of the artist''s rank in the fan''s Spotify top artists: rank 1 earns full marks, rank saturation earns 1/saturation'),
  ('spotify_recent_play', 1, 50,
   'Weight of plays among the fan''s last 50 Spotify plays (full marks at saturation, log curve)');

-- Spotify rules are exactly the five signals the view knows, each with a saturation and a weight of 0 or more;
-- ledger rules have no saturation.
ALTER TABLE scoring_rules
  ADD CONSTRAINT scoring_rules_spotify_known CHECK (rule_key NOT LIKE 'spotify\_%' OR rule_key IN
    ('spotify_liked_track', 'spotify_playlist_track', 'spotify_album_depth', 'spotify_top_artist', 'spotify_recent_play')),
  ADD CONSTRAINT scoring_rules_spotify_weight CHECK (rule_key NOT LIKE 'spotify\_%' OR (saturation IS NOT NULL AND points >= 0)),
  ADD CONSTRAINT scoring_rules_ledger_saturation CHECK (rule_key LIKE 'spotify\_%' OR saturation IS NULL);

-- Per fan and active artist: each signal's share (0..1), the weighted Spotify score (0..100), ledger points, and
-- the raw score the scoring worker ranks. Inactive rules drop out of the weights entirely. Deleted accounts are
-- left out (their ledger rows stay until the purge). Reads every fan's rows: restructure per artist if it gets slow.
DROP VIEW v_raw_scores;
CREATE VIEW v_raw_scores AS
WITH r AS (
  SELECT rule_key, points AS weight, saturation FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%'
), w AS (
  SELECT max(weight) FILTER (WHERE rule_key = 'spotify_liked_track')        AS liked_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_liked_track')    AS liked_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_playlist_track')     AS playlist_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_playlist_track') AS playlist_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_album_depth')        AS depth_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_album_depth')    AS depth_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_top_artist')         AS top_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_top_artist')     AS top_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_recent_play')        AS recent_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_recent_play')    AS recent_sat,
         sum(weight) AS total_w
    FROM r
), led AS (
  SELECT user_id, artist_id, sum(points) AS pts
    FROM score_events WHERE reversed_at IS NULL
   GROUP BY user_id, artist_id
), catalog AS (
  SELECT artist_id, count(*) AS releases FROM albums WHERE delisted_at IS NULL GROUP BY artist_id
), base AS (
  -- Counts are floored at 0 and a top-artist rank below 1 is ignored, so one bad row can't break an artist's run.
  SELECT COALESCE(s.user_id, l.user_id)        AS user_id,
         COALESCE(s.artist_id, l.artist_id)    AS artist_id,
         greatest(0, COALESCE(s.liked_tracks, 0))    AS liked_tracks,
         greatest(0, COALESCE(s.playlist_tracks, 0)) AS playlist_tracks,
         greatest(0, COALESCE(s.distinct_albums, 0)) AS distinct_albums,
         CASE WHEN s.top_artist_rank >= 1 THEN s.top_artist_rank END AS top_artist_rank,
         greatest(0, COALESCE(s.recent_plays, 0))    AS recent_plays,
         COALESCE(l.pts, 0)                    AS ledger_points,
         COALESCE(c.releases, 0)               AS catalog_releases
    FROM user_artist_signals s
    FULL JOIN led l ON l.user_id = s.user_id AND l.artist_id = s.artist_id
    JOIN artists a ON a.id = COALESCE(s.artist_id, l.artist_id) AND a.status = 'active'
    JOIN users u ON u.id = COALESCE(s.user_id, l.user_id) AND u.deleted_at IS NULL
    LEFT JOIN catalog c ON c.artist_id = a.id
), shares AS (
  SELECT b.*, w.*,
         -- A disabled rule's share is null (least() would otherwise skip the null saturation and give 1).
         CASE WHEN w.liked_sat IS NOT NULL
              THEN least(1, ln(1 + b.liked_tracks::numeric) / ln(1 + w.liked_sat)) END          AS liked_share,
         CASE WHEN w.playlist_sat IS NOT NULL
              THEN least(1, ln(1 + b.playlist_tracks::numeric) / ln(1 + w.playlist_sat)) END    AS playlist_share,
         -- The catalog may be smaller than the saturation; an empty catalog falls back to the saturation.
         CASE WHEN w.depth_sat IS NOT NULL
              THEN least(1, b.distinct_albums / greatest(1, least(w.depth_sat, NULLIF(b.catalog_releases, 0)))) END
                                                                                                AS depth_share,
         CASE WHEN w.top_sat IS NULL THEN NULL
              WHEN b.top_artist_rank IS NULL THEN 0
              ELSE greatest(0, least(1, (w.top_sat + 1 - b.top_artist_rank) / w.top_sat)) END  AS top_share,
         CASE WHEN w.recent_sat IS NOT NULL
              THEN least(1, ln(1 + b.recent_plays::numeric) / ln(1 + w.recent_sat)) END         AS recent_share
    FROM base b CROSS JOIN w
)
SELECT user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums, top_artist_rank, recent_plays,
       catalog_releases,
       round(liked_share, 4) AS liked_share, round(playlist_share, 4) AS playlist_share,
       round(depth_share, 4) AS depth_share, round(top_share, 4) AS top_share,
       round(recent_share, 4) AS recent_share,
       spotify_score, ledger_points, spotify_score + ledger_points AS raw_score
  FROM shares,
  LATERAL (SELECT round(COALESCE(100 * (
             COALESCE(liked_w * liked_share, 0) + COALESCE(playlist_w * playlist_share, 0)
           + COALESCE(depth_w * depth_share, 0) + COALESCE(top_w * top_share, 0)
           + COALESCE(recent_w * recent_share, 0)) / NULLIF(total_w, 0), 0), 2) AS spotify_score) score;

-- Queues a rescore of one artist's fans (percentiles depend on everyone), at most one queued per artist.
-- Returns the job id, or null when one was already queued.
CREATE FUNCTION request_artist_rescore(p_artist uuid) RETURNS bigint LANGUAGE sql AS $$
  SELECT enqueue_job('scoring.score_artist', jsonb_build_object('artist_id', p_artist), 'scoring.score_artist:' || p_artist);
$$;

-- Requests a rescore of every active artist; returns how many artists that is. Used when Spotify weights change
-- and by the daily safety-net schedule.
CREATE FUNCTION request_full_rescore() RETURNS integer LANGUAGE sql AS $$
  SELECT count(*)::integer FROM (SELECT request_artist_rescore(id) FROM artists WHERE status = 'active') queued;
$$;

-- Only changes that move scores queue a rescore: a Spotify rule added, removed, or with a new weight, saturation,
-- or active flag. Ledger rules don't (awards keep the points they were given), nor do description edits.
CREATE FUNCTION trg_scoring_rules_rescore() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM request_full_rescore();
  RETURN NULL;
END $$;
CREATE TRIGGER trg_scoring_rules_rescore_insert AFTER INSERT ON scoring_rules
  FOR EACH ROW WHEN (NEW.rule_key LIKE 'spotify\_%') EXECUTE FUNCTION trg_scoring_rules_rescore();
CREATE TRIGGER trg_scoring_rules_rescore_delete AFTER DELETE ON scoring_rules
  FOR EACH ROW WHEN (OLD.rule_key LIKE 'spotify\_%') EXECUTE FUNCTION trg_scoring_rules_rescore();
CREATE TRIGGER trg_scoring_rules_rescore_update AFTER UPDATE ON scoring_rules
  FOR EACH ROW WHEN ((OLD.rule_key LIKE 'spotify\_%' OR NEW.rule_key LIKE 'spotify\_%')
                     AND (OLD.points, OLD.saturation, OLD.active, OLD.rule_key)
                         IS DISTINCT FROM (NEW.points, NEW.saturation, NEW.active, NEW.rule_key))
  EXECUTE FUNCTION trg_scoring_rules_rescore();

-- An artist leaving active (paused) gets a run that removes its fans' scores.
CREATE FUNCTION trg_artist_unscore() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM request_artist_rescore(NEW.id);
  RETURN NULL;
END $$;
CREATE TRIGGER trg_artist_unscore AFTER UPDATE OF status ON artists
  FOR EACH ROW WHEN (OLD.status = 'active' AND NEW.status <> 'active') EXECUTE FUNCTION trg_artist_unscore();

-- Score of every fan of active artists from now on.
SELECT request_full_rescore();
