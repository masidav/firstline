-- More Spotify signals for the fandom score (decided 2026-10-24; see workers/scoring/README.md and ADR 0005).
-- The sync stores per-artist counts in user_artist_signals, catalog album saves in user_album_saves, and the hourly
-- poll keeps a play ledger in user_plays (Spotify only ever returns the last 50 plays). v_raw_scores turns them into
-- shares. Only tracked artists' data is ever stored; the worker drops the rest in memory.

ALTER TABLE user_artist_signals
  ADD COLUMN top_artist_rank_short smallint,                        -- rank in the fan's last ~4 weeks
  ADD COLUMN top_artist_rank_long  smallint,                        -- rank over the fan's listening history
  ADD COLUMN top_tracks            integer NOT NULL DEFAULT 0,       -- distinct tracks by the artist among the fan's top tracks (any range)
  ADD COLUMN playlists_featuring   integer NOT NULL DEFAULT 0,       -- own/collaborative playlists with a track by the artist
  ADD COLUMN dedicated_playlists   integer NOT NULL DEFAULT 0,       -- of those, playlists that are mostly the artist (>= 80 % of >= 5 tracks)
  ADD COLUMN followed              boolean NOT NULL DEFAULT false;   -- follows the artist on Spotify (needs scope user-follow-read)

-- Catalog albums the fan saved, replaced per sync like user_track_likes. saved_at drives early-save and tenure.
CREATE TABLE user_album_saves (
  user_id  uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  album_id uuid NOT NULL REFERENCES albums ON DELETE CASCADE,
  saved_at timestamptz,
  PRIMARY KEY (user_id, album_id)
);

-- Play ledger for tracked artists: seeded by each sync from the last 50 plays and extended hourly by
-- spotify.poll_plays, which reads only plays after the stored cursor. Rows older than the retention are pruned.
CREATE TABLE user_plays (
  user_id          uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  artist_id        uuid NOT NULL REFERENCES artists ON DELETE CASCADE,
  spotify_track_id text NOT NULL,
  played_at        timestamptz NOT NULL,
  PRIMARY KEY (user_id, played_at, spotify_track_id)
);
CREATE INDEX user_plays_artist_idx ON user_plays (user_id, artist_id, played_at DESC);

ALTER TABLE spotify_connections
  ADD COLUMN plays_cursor    bigint,        -- Spotify "after" cursor (ms since epoch) of the newest play stored
  ADD COLUMN plays_polled_at timestamptz;   -- last poll attempt (hourly schedule)

-- The new rules start as an even split with the five existing ones (weights expected to change often).
ALTER TABLE scoring_rules DROP CONSTRAINT scoring_rules_spotify_known;
ALTER TABLE scoring_rules
  ADD CONSTRAINT scoring_rules_spotify_known CHECK (rule_key NOT LIKE 'spotify\_%' OR rule_key IN
    ('spotify_liked_track', 'spotify_playlist_track', 'spotify_album_depth', 'spotify_top_artist', 'spotify_recent_play',
     'spotify_top_artist_short', 'spotify_top_artist_long', 'spotify_top_track', 'spotify_saved_album',
     'spotify_early_save', 'spotify_tenure', 'spotify_playlist_feature', 'spotify_dedicated_playlist', 'spotify_follow',
     'spotify_play_history', 'spotify_play_streak', 'spotify_catalog_coverage', 'spotify_deep_cut'));

INSERT INTO scoring_rules (rule_key, points, saturation, description) VALUES
  ('spotify_top_artist_short', 1, 50,
   'Weight of the artist''s rank in the fan''s top artists of the last 4 weeks: rank 1 full marks, rank saturation 1/saturation'),
  ('spotify_top_artist_long', 1, 50,
   'Weight of the artist''s rank in the fan''s all-time top artists: rank 1 full marks, rank saturation 1/saturation'),
  ('spotify_top_track', 1, 20,
   'Weight of distinct tracks by the artist among the fan''s top tracks, any time range (full marks at saturation, log curve)'),
  ('spotify_saved_album', 1, 10,
   'Weight of the artist''s albums the fan saved, out of the catalog or saturation, whichever is smaller (linear)'),
  ('spotify_early_save', 1, 10,
   'Weight of releases the fan saved within a week of release day (full marks at saturation, log curve)'),
  ('spotify_tenure', 1, 730,
   'Weight of how long the fan has had the artist in their library: days since the earliest save, full marks at saturation days (linear)'),
  ('spotify_playlist_feature', 1, 5,
   'Weight of the fan''s own/collaborative playlists that include the artist (full marks at saturation, log curve)'),
  ('spotify_dedicated_playlist', 1, 1,
   'Weight of playlists that are mostly the artist (80 % of at least 5 tracks), full marks at saturation (linear)'),
  ('spotify_follow', 1, 1,
   'Weight of following the artist on Spotify (1 when followed)'),
  ('spotify_play_history', 1, 100,
   'Weight of plays of the artist in the last 90 days, from the play ledger (full marks at saturation, log curve)'),
  ('spotify_play_streak', 1, 12,
   'Weight of consecutive weeks with a play of the artist, full marks at saturation weeks (linear)'),
  ('spotify_catalog_coverage', 1, 50,
   'Weight of catalog tracks the fan saved or played, out of the catalog or saturation, whichever is smaller (linear)'),
  ('spotify_deep_cut', 1, 10,
   'Weight of the fan''s saved or played tracks that at most 10 % of the artist''s fans here have, once the artist has 10 fans (log curve)');

-- Same contract as before plus the new counts and shares. Reads every fan's rows before filtering to one artist;
-- restructure per artist if it gets slow (workers/scoring/README.md).
DROP VIEW v_raw_scores;
CREATE VIEW v_raw_scores AS
WITH r AS (
  SELECT rule_key, points AS weight, saturation FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%'
), w AS (
  SELECT max(weight) FILTER (WHERE rule_key = 'spotify_liked_track')            AS liked_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_liked_track')        AS liked_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_playlist_track')         AS playlist_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_playlist_track')     AS playlist_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_album_depth')            AS depth_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_album_depth')        AS depth_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_top_artist')             AS top_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_top_artist')         AS top_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_recent_play')            AS recent_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_recent_play')        AS recent_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_top_artist_short')       AS top_short_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_top_artist_short')   AS top_short_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_top_artist_long')        AS top_long_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_top_artist_long')    AS top_long_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_top_track')              AS top_track_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_top_track')          AS top_track_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_saved_album')            AS saved_album_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_saved_album')        AS saved_album_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_early_save')             AS early_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_early_save')         AS early_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_tenure')                 AS tenure_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_tenure')             AS tenure_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_playlist_feature')       AS feature_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_playlist_feature')   AS feature_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_dedicated_playlist')     AS dedicated_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_dedicated_playlist') AS dedicated_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_follow')                 AS follow_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_follow')             AS follow_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_play_history')           AS plays_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_play_history')       AS plays_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_play_streak')            AS streak_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_play_streak')        AS streak_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_catalog_coverage')       AS coverage_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_catalog_coverage')   AS coverage_sat,
         max(weight) FILTER (WHERE rule_key = 'spotify_deep_cut')               AS deep_w,
         max(saturation) FILTER (WHERE rule_key = 'spotify_deep_cut')           AS deep_sat,
         sum(weight) AS total_w
    FROM r
), led AS (
  SELECT user_id, artist_id, sum(points) AS pts
    FROM score_events WHERE reversed_at IS NULL
   GROUP BY user_id, artist_id
), catalog AS (
  SELECT al.artist_id, count(DISTINCT al.id) AS releases, count(t.id) AS tracks
    FROM albums al LEFT JOIN tracks t ON t.album_id = al.id
   WHERE al.delisted_at IS NULL
   GROUP BY al.artist_id
), album_saves AS (
  SELECT s.user_id, al.artist_id, count(*) AS saved_albums
    FROM user_album_saves s JOIN albums al ON al.id = s.album_id AND al.delisted_at IS NULL
   GROUP BY s.user_id, al.artist_id
), saves AS (
  -- Every dated save of a catalog release: liked tracks and saved albums.
  SELECT l.user_id, al.artist_id, al.id AS album_id, l.saved_at, al.release_date, al.release_date_precision
    FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
   WHERE l.source = 'liked' AND l.saved_at IS NOT NULL
  UNION ALL
  SELECT s.user_id, al.artist_id, al.id, s.saved_at, al.release_date, al.release_date_precision
    FROM user_album_saves s JOIN albums al ON al.id = s.album_id
   WHERE s.saved_at IS NOT NULL
), save_stats AS (
  SELECT user_id, artist_id, min(saved_at) AS first_saved_at,
         -- Within a week of release day; only releases dated to the day can tell.
         count(DISTINCT album_id) FILTER (WHERE release_date_precision = 'day'
                                            AND saved_at >= release_date::timestamptz - interval '1 day'
                                            AND saved_at <  release_date::timestamptz + interval '8 days') AS early_saves
    FROM saves
   GROUP BY user_id, artist_id
), plays AS (
  SELECT user_id, artist_id, count(*) AS plays_90d
    FROM user_plays WHERE played_at >= now() - interval '90 days'
   GROUP BY user_id, artist_id
), play_weeks AS (
  SELECT DISTINCT user_id, artist_id, date_trunc('week', played_at) AS wk
    FROM user_plays WHERE played_at >= now() - interval '400 days'
), streaks AS (
  -- Consecutive weeks counted back from this week, or from last week when this week has no play yet.
  SELECT user_id, artist_id, count(*) AS streak_weeks
    FROM (SELECT user_id, artist_id, wk,
                 row_number() OVER (PARTITION BY user_id, artist_id ORDER BY wk DESC) AS rn,
                 CASE WHEN max(wk) OVER (PARTITION BY user_id, artist_id) = date_trunc('week', now())
                      THEN date_trunc('week', now()) ELSE date_trunc('week', now()) - interval '1 week' END AS anchor
            FROM play_weeks) x
   WHERE wk = anchor - (rn - 1) * interval '1 week'
   GROUP BY user_id, artist_id
), touches AS (
  -- Catalog tracks the fan saved, put in a playlist, or played.
  SELECT l.user_id, al.artist_id, t.id AS track_id
    FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
  UNION
  SELECT p.user_id, al.artist_id, t.id
    FROM user_plays p JOIN tracks t ON t.spotify_track_id = p.spotify_track_id JOIN albums al ON al.id = t.album_id
), track_fans AS (
  SELECT artist_id, track_id, count(*) AS fans FROM touches GROUP BY artist_id, track_id
), artist_fans AS (
  SELECT artist_id, count(DISTINCT user_id) AS fans FROM touches GROUP BY artist_id
), coverage AS (
  SELECT tc.user_id, tc.artist_id, count(*) AS tracks_touched,
         count(*) FILTER (WHERE af.fans >= 10 AND tf.fans * 10 <= af.fans) AS deep_cuts
    FROM touches tc
    JOIN track_fans tf ON tf.artist_id = tc.artist_id AND tf.track_id = tc.track_id
    JOIN artist_fans af ON af.artist_id = tc.artist_id
   GROUP BY tc.user_id, tc.artist_id
), keys AS (
  SELECT user_id, artist_id FROM user_artist_signals
  UNION SELECT user_id, artist_id FROM led
  UNION SELECT user_id, artist_id FROM plays
  UNION SELECT user_id, artist_id FROM album_saves
), base AS (
  -- Counts are floored at 0 and a top-artist rank below 1 is ignored, so one bad row can't break an artist's run.
  SELECT k.user_id, k.artist_id,
         greatest(0, COALESCE(s.liked_tracks, 0))    AS liked_tracks,
         greatest(0, COALESCE(s.playlist_tracks, 0)) AS playlist_tracks,
         greatest(0, COALESCE(s.distinct_albums, 0)) AS distinct_albums,
         CASE WHEN s.top_artist_rank >= 1 THEN s.top_artist_rank END             AS top_artist_rank,
         greatest(0, COALESCE(s.recent_plays, 0))    AS recent_plays,
         CASE WHEN s.top_artist_rank_short >= 1 THEN s.top_artist_rank_short END AS top_artist_rank_short,
         CASE WHEN s.top_artist_rank_long >= 1 THEN s.top_artist_rank_long END   AS top_artist_rank_long,
         greatest(0, COALESCE(s.top_tracks, 0))          AS top_tracks,
         greatest(0, COALESCE(s.playlists_featuring, 0)) AS playlists_featuring,
         greatest(0, COALESCE(s.dedicated_playlists, 0)) AS dedicated_playlists,
         COALESCE(s.followed, false)                     AS followed,
         COALESCE(sa.saved_albums, 0)                    AS saved_albums,
         COALESCE(ss.early_saves, 0)                     AS early_saves,
         ss.first_saved_at,
         greatest(0, floor(extract(epoch FROM now() - ss.first_saved_at) / 86400))::integer AS tenure_days,
         COALESCE(p.plays_90d, 0)                        AS plays_90d,
         COALESCE(st.streak_weeks, 0)                    AS streak_weeks,
         COALESCE(cv.tracks_touched, 0)                  AS tracks_touched,
         COALESCE(cv.deep_cuts, 0)                       AS deep_cuts,
         COALESCE(l.pts, 0)                              AS ledger_points,
         COALESCE(c.releases, 0)                         AS catalog_releases,
         COALESCE(c.tracks, 0)                           AS catalog_tracks
    FROM keys k
    JOIN artists a ON a.id = k.artist_id AND a.status = 'active'
    JOIN users u ON u.id = k.user_id AND u.deleted_at IS NULL
    LEFT JOIN user_artist_signals s ON s.user_id = k.user_id AND s.artist_id = k.artist_id
    LEFT JOIN led l ON l.user_id = k.user_id AND l.artist_id = k.artist_id
    LEFT JOIN album_saves sa ON sa.user_id = k.user_id AND sa.artist_id = k.artist_id
    LEFT JOIN save_stats ss ON ss.user_id = k.user_id AND ss.artist_id = k.artist_id
    LEFT JOIN plays p ON p.user_id = k.user_id AND p.artist_id = k.artist_id
    LEFT JOIN streaks st ON st.user_id = k.user_id AND st.artist_id = k.artist_id
    LEFT JOIN coverage cv ON cv.user_id = k.user_id AND cv.artist_id = k.artist_id
    LEFT JOIN catalog c ON c.artist_id = k.artist_id
), shares AS (
  SELECT b.*, w.*,
         -- A disabled rule's share is null (least() would otherwise skip the null saturation and give 1).
         CASE WHEN w.liked_sat IS NOT NULL
              THEN least(1, ln(1 + b.liked_tracks::numeric) / ln(1 + w.liked_sat)) END            AS liked_share,
         CASE WHEN w.playlist_sat IS NOT NULL
              THEN least(1, ln(1 + b.playlist_tracks::numeric) / ln(1 + w.playlist_sat)) END      AS playlist_share,
         -- The catalog may be smaller than the saturation; an empty catalog falls back to the saturation.
         CASE WHEN w.depth_sat IS NOT NULL
              THEN least(1, b.distinct_albums / greatest(1, least(w.depth_sat, NULLIF(b.catalog_releases, 0)))) END
                                                                                                  AS depth_share,
         CASE WHEN w.top_sat IS NULL THEN NULL
              WHEN b.top_artist_rank IS NULL THEN 0
              ELSE greatest(0, least(1, (w.top_sat + 1 - b.top_artist_rank) / w.top_sat)) END    AS top_share,
         CASE WHEN w.recent_sat IS NOT NULL
              THEN least(1, ln(1 + b.recent_plays::numeric) / ln(1 + w.recent_sat)) END           AS recent_share,
         CASE WHEN w.top_short_sat IS NULL THEN NULL
              WHEN b.top_artist_rank_short IS NULL THEN 0
              ELSE greatest(0, least(1, (w.top_short_sat + 1 - b.top_artist_rank_short) / w.top_short_sat)) END
                                                                                                  AS top_short_share,
         CASE WHEN w.top_long_sat IS NULL THEN NULL
              WHEN b.top_artist_rank_long IS NULL THEN 0
              ELSE greatest(0, least(1, (w.top_long_sat + 1 - b.top_artist_rank_long) / w.top_long_sat)) END
                                                                                                  AS top_long_share,
         CASE WHEN w.top_track_sat IS NOT NULL
              THEN least(1, ln(1 + b.top_tracks::numeric) / ln(1 + w.top_track_sat)) END          AS top_track_share,
         CASE WHEN w.saved_album_sat IS NOT NULL
              THEN least(1, b.saved_albums / greatest(1, least(w.saved_album_sat, NULLIF(b.catalog_releases, 0)))) END
                                                                                                  AS saved_album_share,
         CASE WHEN w.early_sat IS NOT NULL
              THEN least(1, ln(1 + b.early_saves::numeric) / ln(1 + w.early_sat)) END             AS early_save_share,
         CASE WHEN w.tenure_sat IS NOT NULL
              THEN least(1, b.tenure_days / w.tenure_sat) END                                      AS tenure_share,
         CASE WHEN w.feature_sat IS NOT NULL
              THEN least(1, ln(1 + b.playlists_featuring::numeric) / ln(1 + w.feature_sat)) END   AS playlist_feature_share,
         CASE WHEN w.dedicated_sat IS NOT NULL
              THEN least(1, b.dedicated_playlists / w.dedicated_sat) END                           AS dedicated_playlist_share,
         CASE WHEN w.follow_sat IS NOT NULL
              THEN CASE WHEN b.followed THEN 1 ELSE 0 END::numeric END                             AS follow_share,
         CASE WHEN w.plays_sat IS NOT NULL
              THEN least(1, ln(1 + b.plays_90d::numeric) / ln(1 + w.plays_sat)) END               AS play_history_share,
         CASE WHEN w.streak_sat IS NOT NULL
              THEN least(1, b.streak_weeks / w.streak_sat) END                                     AS play_streak_share,
         CASE WHEN w.coverage_sat IS NOT NULL
              THEN least(1, b.tracks_touched / greatest(1, least(w.coverage_sat, NULLIF(b.catalog_tracks, 0)))) END
                                                                                                  AS catalog_coverage_share,
         CASE WHEN w.deep_sat IS NOT NULL
              THEN least(1, ln(1 + b.deep_cuts::numeric) / ln(1 + w.deep_sat)) END                AS deep_cut_share
    FROM base b CROSS JOIN w
)
SELECT user_id, artist_id, liked_tracks, playlist_tracks, distinct_albums, top_artist_rank, recent_plays,
       top_artist_rank_short, top_artist_rank_long, top_tracks, saved_albums, early_saves, first_saved_at, tenure_days,
       playlists_featuring, dedicated_playlists, followed, plays_90d, streak_weeks, tracks_touched, deep_cuts,
       catalog_releases, catalog_tracks,
       round(liked_share, 4) AS liked_share, round(playlist_share, 4) AS playlist_share,
       round(depth_share, 4) AS depth_share, round(top_share, 4) AS top_share,
       round(recent_share, 4) AS recent_share,
       round(top_short_share, 4) AS top_short_share, round(top_long_share, 4) AS top_long_share,
       round(top_track_share, 4) AS top_track_share, round(saved_album_share, 4) AS saved_album_share,
       round(early_save_share, 4) AS early_save_share, round(tenure_share, 4) AS tenure_share,
       round(playlist_feature_share, 4) AS playlist_feature_share,
       round(dedicated_playlist_share, 4) AS dedicated_playlist_share, round(follow_share, 4) AS follow_share,
       round(play_history_share, 4) AS play_history_share, round(play_streak_share, 4) AS play_streak_share,
       round(catalog_coverage_share, 4) AS catalog_coverage_share, round(deep_cut_share, 4) AS deep_cut_share,
       spotify_score, ledger_points, spotify_score + ledger_points AS raw_score
  FROM shares,
  LATERAL (SELECT round(COALESCE(100 * (
             COALESCE(liked_w * liked_share, 0) + COALESCE(playlist_w * playlist_share, 0)
           + COALESCE(depth_w * depth_share, 0) + COALESCE(top_w * top_share, 0)
           + COALESCE(recent_w * recent_share, 0)
           + COALESCE(top_short_w * top_short_share, 0) + COALESCE(top_long_w * top_long_share, 0)
           + COALESCE(top_track_w * top_track_share, 0) + COALESCE(saved_album_w * saved_album_share, 0)
           + COALESCE(early_w * early_save_share, 0) + COALESCE(tenure_w * tenure_share, 0)
           + COALESCE(feature_w * playlist_feature_share, 0) + COALESCE(dedicated_w * dedicated_playlist_share, 0)
           + COALESCE(follow_w * follow_share, 0)
           + COALESCE(plays_w * play_history_share, 0) + COALESCE(streak_w * play_streak_share, 0)
           + COALESCE(coverage_w * catalog_coverage_share, 0) + COALESCE(deep_w * deep_cut_share, 0)
           ) / NULLIF(total_w, 0), 0), 2) AS spotify_score) score;

-- Scores of every fan of active artists are recomputed with the new rules.
SELECT request_full_rescore();

-- The raw score a fan would have from saving p_saves of the artist's albums and nothing else, under the current
-- rules (used by allocations as an eligibility threshold, so it moves with weight changes). Saved albums are the
-- linear signal spotify_saved_album: share = saves / min(saturation, catalog releases), weighted like any other.
CREATE FUNCTION album_saves_score(p_artist uuid, p_saves integer) RETURNS numeric LANGUAGE sql STABLE AS $$
  WITH r AS (
    SELECT points AS weight, saturation FROM scoring_rules WHERE active AND rule_key LIKE 'spotify\_%'
  ), rule AS (
    SELECT points AS weight, saturation FROM scoring_rules WHERE active AND rule_key = 'spotify_saved_album'
  ), catalog AS (
    SELECT count(*) AS releases FROM albums WHERE artist_id = p_artist AND delisted_at IS NULL
  )
  SELECT round(COALESCE(100 * rule.weight
           * least(1, greatest(0, p_saves) / greatest(1, least(rule.saturation, NULLIF(catalog.releases, 0))))
           / NULLIF((SELECT sum(weight) FROM r), 0), 0), 2)
    FROM catalog LEFT JOIN rule ON true;
$$;
