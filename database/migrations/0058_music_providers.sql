-- Apple Music and Tidal as listening sources (Phase 10, "add verification sources"; requested 2026-11-05). Design:
-- docs/adr/0007-music-providers.md.
--
-- Until now every listening signal came from Spotify and the signal tables were keyed by fan and artist. Now:
-- - music_connections: one row per fan and provider ('apple_music', 'tidal'), with the encrypted token (Apple: the
--   Music User Token, long-lived, no refresh; Tidal: the refresh token), scopes, and the same sync state machine as
--   spotify_connections. Tidal's user id is unique per provider, as Spotify's is; Apple Music has no user id API, so
--   one Apple Music library can be connected to several fan accounts (known limitation, ADR 0007).
-- - user_artist_signals, user_track_likes, user_album_saves, user_plays get a provider column ('spotify' for every row
--   that exists) in their primary key, so each provider keeps its own rows and a sync replaces only its own.
-- - v_raw_scores reads a fan's strongest signal per artist across providers (max of each count, best rank, any
--   follow), so nothing is counted twice when the same track is liked on two services, and a fan on one service is
--   scored exactly as before. Saves and plays union across providers (distinct tracks and albums; a play on each
--   service is a play).
-- - tracks.isrc and albums.upc: the recording and release codes Apple Music and Tidal give for every item, stored
--   when a title match finds the catalog track, so later matches from any provider go by code first (Spotify's
--   endpoints available to this app don't return them, ADR 0007).
-- - The Tidal account history joins spotify_account_links (hashed 'tidal:<id>'), so the spotify_account_reuse rule
--   also sees one Tidal account passed through several fan accounts.

-- ---------------------------------------------------------------- connections
CREATE TABLE music_connections (
  user_id               uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  provider              text NOT NULL CONSTRAINT music_connections_provider_check CHECK (provider IN ('apple_music', 'tidal')),
  provider_user_id      text,                                  -- Tidal: the user id; Apple Music: none
  token_enc             bytea NOT NULL,                        -- encrypted app-side (SpotifyTokenProtector, purpose per provider)
  scopes                text[] NOT NULL DEFAULT '{}',
  storefront            text,                                  -- Apple Music: the fan's storefront (country code)
  connected_at          timestamptz NOT NULL DEFAULT now(),
  last_synced_at        timestamptz,
  sync_state            text NOT NULL DEFAULT 'idle'
    CONSTRAINT music_connections_sync_state CHECK (sync_state IN ('idle', 'queued', 'running', 'failed', 'reauth_required')),
  sync_error            text
    CONSTRAINT music_connections_sync_error CHECK (sync_error IN ('provider_unavailable', 'provider_forbidden', 'provider_reauth_required', 'internal_error')),
  sync_requested_at     timestamptz,
  last_sync_attempt_at  timestamptz,
  tracked_artists_found integer NOT NULL DEFAULT 0 CHECK (tracked_artists_found >= 0),
  PRIMARY KEY (user_id, provider),
  CONSTRAINT music_connections_tidal_user_check CHECK (provider <> 'tidal' OR provider_user_id IS NOT NULL)
);
CREATE UNIQUE INDEX music_connections_provider_user_key ON music_connections (provider, provider_user_id) WHERE provider_user_id IS NOT NULL;
CREATE INDEX music_connections_due_idx ON music_connections (last_sync_attempt_at NULLS FIRST) WHERE sync_state IN ('idle', 'failed');

-- Marks the connection queued and enqueues its sync job, at most one queued per fan and provider. Returns false
-- when the fan has no such connection.
CREATE FUNCTION request_music_sync(p_user uuid, p_provider text) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
  UPDATE music_connections SET sync_state = 'queued', sync_error = NULL WHERE user_id = p_user AND provider = p_provider;
  IF NOT FOUND THEN RETURN false; END IF;
  PERFORM enqueue_job('music.sync', jsonb_build_object('user_id', p_user, 'provider', p_provider),
                      'music.sync:' || p_provider || ':' || p_user);
  RETURN true;
END $$;

-- Deleting the account removes the connections at once (the hard delete cascades; this covers the soft delete).
CREATE FUNCTION trg_user_delete_music() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM music_connections WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_delete_music AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_delete_music();

-- Account history for the reuse rule (0054): a Tidal account connected by several fan accounts.
CREATE FUNCTION trg_music_account_link() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.provider_user_id IS NOT NULL THEN
    INSERT INTO spotify_account_links (account_hash, user_id)
    VALUES (spotify_account_hash(NEW.provider || ':' || NEW.provider_user_id), NEW.user_id)
    ON CONFLICT (account_hash, user_id) DO UPDATE SET last_linked_at = greatest(spotify_account_links.last_linked_at, now());
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_music_account_link AFTER INSERT OR UPDATE OF provider_user_id ON music_connections
  FOR EACH ROW EXECUTE FUNCTION trg_music_account_link();

-- ---------------------------------------------------------------- catalog codes
ALTER TABLE tracks ADD COLUMN isrc text CHECK (isrc ~ '^[A-Z0-9]{12}$');
CREATE INDEX tracks_isrc_idx ON tracks (isrc) WHERE isrc IS NOT NULL;
ALTER TABLE albums ADD COLUMN upc text CHECK (upc ~ '^[0-9]{8,14}$');
CREATE INDEX albums_upc_idx ON albums (upc) WHERE upc IS NOT NULL;

-- ---------------------------------------------------------------- provider on the signal tables
ALTER TABLE user_artist_signals ADD COLUMN provider text NOT NULL DEFAULT 'spotify'
  CONSTRAINT user_artist_signals_provider_check CHECK (provider IN ('spotify', 'apple_music', 'tidal'));
ALTER TABLE user_artist_signals DROP CONSTRAINT user_artist_signals_pkey, ADD PRIMARY KEY (user_id, provider, artist_id);
CREATE INDEX user_artist_signals_artist_idx ON user_artist_signals (artist_id);

ALTER TABLE user_track_likes ADD COLUMN provider text NOT NULL DEFAULT 'spotify'
  CONSTRAINT user_track_likes_provider_check CHECK (provider IN ('spotify', 'apple_music', 'tidal'));
ALTER TABLE user_track_likes DROP CONSTRAINT user_track_likes_pkey, ADD PRIMARY KEY (user_id, provider, track_id, source);

ALTER TABLE user_album_saves ADD COLUMN provider text NOT NULL DEFAULT 'spotify'
  CONSTRAINT user_album_saves_provider_check CHECK (provider IN ('spotify', 'apple_music', 'tidal'));
ALTER TABLE user_album_saves DROP CONSTRAINT user_album_saves_pkey, ADD PRIMARY KEY (user_id, provider, album_id);

ALTER TABLE user_plays ADD COLUMN provider text NOT NULL DEFAULT 'spotify'
  CONSTRAINT user_plays_provider_check CHECK (provider IN ('spotify', 'apple_music', 'tidal'));
ALTER TABLE user_plays DROP CONSTRAINT user_plays_pkey, ADD PRIMARY KEY (user_id, provider, played_at, spotify_track_id);

-- ---------------------------------------------------------------- the score
-- Same contract as 0015. Per fan and artist, the strongest signal across providers: the greatest count, the best
-- rank, a follow on any service. Saves, plays, and catalog touches union across providers.
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
), signals AS (
  -- The strongest signal per artist across the fan's connected services.
  SELECT user_id, artist_id,
         max(liked_tracks) AS liked_tracks, max(playlist_tracks) AS playlist_tracks, max(distinct_albums) AS distinct_albums,
         min(top_artist_rank) FILTER (WHERE top_artist_rank >= 1) AS top_artist_rank,
         max(recent_plays) AS recent_plays,
         min(top_artist_rank_short) FILTER (WHERE top_artist_rank_short >= 1) AS top_artist_rank_short,
         min(top_artist_rank_long) FILTER (WHERE top_artist_rank_long >= 1) AS top_artist_rank_long,
         max(top_tracks) AS top_tracks, max(playlists_featuring) AS playlists_featuring,
         max(dedicated_playlists) AS dedicated_playlists, bool_or(followed) AS followed
    FROM user_artist_signals
   GROUP BY user_id, artist_id
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
  SELECT s.user_id, al.artist_id, count(DISTINCT s.album_id) AS saved_albums
    FROM user_album_saves s JOIN albums al ON al.id = s.album_id AND al.delisted_at IS NULL
   GROUP BY s.user_id, al.artist_id
), saves AS (
  -- Every dated save of a catalog release: liked tracks and saved albums, on any service.
  SELECT l.user_id, al.artist_id, al.id AS album_id, l.saved_at, al.release_date, al.release_date_precision
    FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
   WHERE l.source = 'liked' AND l.saved_at IS NOT NULL
  UNION ALL
  SELECT s.user_id, al.artist_id, al.id, s.saved_at, al.release_date, al.release_date_precision
    FROM user_album_saves s JOIN albums al ON al.id = s.album_id
   WHERE s.saved_at IS NOT NULL
), save_stats AS (
  SELECT user_id, artist_id, min(saved_at) AS first_saved_at,
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
  SELECT user_id, artist_id, count(*) AS streak_weeks
    FROM (SELECT user_id, artist_id, wk,
                 row_number() OVER (PARTITION BY user_id, artist_id ORDER BY wk DESC) AS rn,
                 CASE WHEN max(wk) OVER (PARTITION BY user_id, artist_id) = date_trunc('week', now())
                      THEN date_trunc('week', now()) ELSE date_trunc('week', now()) - interval '1 week' END AS anchor
            FROM play_weeks) x
   WHERE wk = anchor - (rn - 1) * interval '1 week'
   GROUP BY user_id, artist_id
), touches AS (
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
  SELECT user_id, artist_id FROM signals
  UNION SELECT user_id, artist_id FROM led
  UNION SELECT user_id, artist_id FROM plays
  UNION SELECT user_id, artist_id FROM album_saves
), base AS (
  SELECT k.user_id, k.artist_id,
         greatest(0, COALESCE(s.liked_tracks, 0))    AS liked_tracks,
         greatest(0, COALESCE(s.playlist_tracks, 0)) AS playlist_tracks,
         greatest(0, COALESCE(s.distinct_albums, 0)) AS distinct_albums,
         s.top_artist_rank,
         greatest(0, COALESCE(s.recent_plays, 0))    AS recent_plays,
         s.top_artist_rank_short,
         s.top_artist_rank_long,
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
    LEFT JOIN signals s ON s.user_id = k.user_id AND s.artist_id = k.artist_id
    LEFT JOIN led l ON l.user_id = k.user_id AND l.artist_id = k.artist_id
    LEFT JOIN album_saves sa ON sa.user_id = k.user_id AND sa.artist_id = k.artist_id
    LEFT JOIN save_stats ss ON ss.user_id = k.user_id AND ss.artist_id = k.artist_id
    LEFT JOIN plays p ON p.user_id = k.user_id AND p.artist_id = k.artist_id
    LEFT JOIN streaks st ON st.user_id = k.user_id AND st.artist_id = k.artist_id
    LEFT JOIN coverage cv ON cv.user_id = k.user_id AND cv.artist_id = k.artist_id
    LEFT JOIN catalog c ON c.artist_id = k.artist_id
), shares AS (
  SELECT b.*, w.*,
         CASE WHEN w.liked_sat IS NOT NULL
              THEN least(1, ln(1 + b.liked_tracks::numeric) / ln(1 + w.liked_sat)) END            AS liked_share,
         CASE WHEN w.playlist_sat IS NOT NULL
              THEN least(1, ln(1 + b.playlist_tracks::numeric) / ln(1 + w.playlist_sat)) END      AS playlist_share,
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

-- The scoring job reads the view unchanged; existing scores are unaffected (one provider per fan today), so no
-- rescore is queued here.
