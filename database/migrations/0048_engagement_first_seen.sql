-- When a playlist add and a follow happened (Phase 10, 2026-10-30; docs/security/gaming-strategies.md strategy 4).
--
-- burst_engagement (0045) could only see liked tracks and album saves, because those are the saves Spotify dates
-- (added_at) and the sync stored: playlist tracks in user_track_likes had source 'playlist' and saved_at NULL, and the
-- follow was a boolean in user_artist_signals. A clicker who adds the whole catalog to a playlist and follows the
-- artist was not seen. Now:
-- - user_track_likes.saved_at on a playlist row is Spotify's added_at for the playlist item (the sync reads it since
--   today; the earliest across the fan's playlists when a track is in several). Spotify omits it for some very old
--   playlists; then the row is stamped when the sync first sees it and keeps that stamp across the daily re-syncs
--   (trg_user_track_likes_first_seen; the sync now updates and prunes rows instead of replacing them). Rows from before
--   this migration stay NULL until the fan's next sync fills them. Scoring is unchanged: early saves and tenure read
--   source 'liked' only (0015).
-- - user_artist_signals.followed_since: when the sync first saw the follow. Set when followed flips to true, kept while
--   it stays true, cleared when it flips to false (trg_user_artist_signals_followed_since), whatever writes the row.
--   Follows known today are stamped with the sync that last saw them (the latest they can have started).
--
-- burst_engagement now counts a fan's playlist adds and the follow alongside liked tracks and album saves, with the same
-- parameters; the evidence breaks the burst down by kind. The sweep also checks fans whose follow is new.

ALTER TABLE user_artist_signals ADD COLUMN followed_since timestamptz;
UPDATE user_artist_signals SET followed_since = synced_at WHERE followed;
CREATE INDEX user_artist_signals_followed_since_idx ON user_artist_signals (followed_since) WHERE followed_since IS NOT NULL;

CREATE FUNCTION trg_user_artist_signals_followed_since() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT NEW.followed THEN
    NEW.followed_since := NULL;
  ELSIF TG_OP = 'UPDATE' AND OLD.followed AND OLD.followed_since IS NOT NULL THEN
    NEW.followed_since := OLD.followed_since;      -- still followed: the first sighting stands
  ELSE
    NEW.followed_since := COALESCE(NEW.followed_since, now());
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_user_artist_signals_followed_since BEFORE INSERT OR UPDATE ON user_artist_signals
  FOR EACH ROW EXECUTE FUNCTION trg_user_artist_signals_followed_since();

-- A playlist row without Spotify's added_at is dated when first seen, and keeps that date. Liked rows are untouched:
-- their saved_at feeds the score, so it stays Spotify's own or null.
CREATE FUNCTION trg_user_track_likes_first_seen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.source = 'playlist' AND NEW.saved_at IS NULL THEN
    NEW.saved_at := CASE WHEN TG_OP = 'UPDATE' THEN COALESCE(OLD.saved_at, now()) ELSE now() END;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER trg_user_track_likes_first_seen BEFORE INSERT OR UPDATE ON user_track_likes
  FOR EACH ROW EXECUTE FUNCTION trg_user_track_likes_first_seen();

UPDATE fraud_rules
   SET description = 'At least min_saves liked tracks, playlist adds, album saves, and the follow of one artist within window_hours, min_share of all the fan''s saves of the artist, within signup_days of sign-up or while the artist has fan tickets open or opening within lead_days',
       updated_at = now()
 WHERE rule_key = 'burst_engagement';

-- Same as 0045, except burst_engagement counts playlist adds and the follow too, and says what kind each save was.
CREATE OR REPLACE FUNCTION fraud_rule_hits(p_user uuid) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE r record; p jsonb; v_hits jsonb := '{}'; v jsonb;
BEGIN
  FOR r IN SELECT * FROM fraud_rules WHERE enabled ORDER BY rule_key LOOP
    p := r.params;
    v := NULL;
    IF r.rule_key = 'round_the_clock' THEN
      SELECT jsonb_build_object('days_over', count(*), 'most_hours', max(hours)) INTO v
        FROM (SELECT count(DISTINCT extract(hour FROM played_at AT TIME ZONE 'UTC')) AS hours
                FROM user_plays WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)
               GROUP BY (played_at AT TIME ZONE 'UTC')::date) d
       WHERE hours >= (p ->> 'min_hours')::int
      HAVING count(*) >= (p ->> 'min_days')::int;
    ELSIF r.rule_key = 'rapid_plays' THEN
      SELECT jsonb_build_object('plays', count(*), 'rapid', count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric),
                                'share', round(count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric)::numeric / count(*), 3)) INTO v
        FROM (SELECT extract(epoch FROM played_at - lag(played_at) OVER (ORDER BY played_at)) AS gap
                FROM user_plays WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)) g
      HAVING count(*) >= (p ->> 'min_plays')::int
         AND count(*) FILTER (WHERE gap < (p ->> 'max_gap_seconds')::numeric) >= (p ->> 'min_share')::numeric * count(*);
    ELSIF r.rule_key = 'looping' THEN
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'plays', total, 'top_share', round(top / total, 3))) INTO v
        FROM (SELECT t.artist_id, sum(t.n)::numeric AS total,
                     (SELECT sum(n) FROM (SELECT count(*) AS n FROM user_plays x
                                           WHERE x.user_id = p_user AND x.artist_id = t.artist_id
                                             AND x.played_at >= now() - make_interval(days => (p ->> 'days')::int)
                                           GROUP BY x.spotify_track_id ORDER BY 1 DESC LIMIT (p ->> 'top_tracks')::int) z)::numeric AS top
                FROM (SELECT artist_id, spotify_track_id, count(*) AS n FROM user_plays
                       WHERE user_id = p_user AND played_at >= now() - make_interval(days => (p ->> 'days')::int)
                       GROUP BY 1, 2) t
               GROUP BY t.artist_id) a
       WHERE total >= (p ->> 'min_plays')::int AND top >= (p ->> 'min_share')::numeric * total
         AND (SELECT count(*) FROM tracks tr JOIN albums al ON al.id = tr.album_id WHERE al.artist_id = a.artist_id)
             >= (p ->> 'min_catalog_tracks')::int;
    ELSIF r.rule_key = 'listening_spike' THEN
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'recent_plays', recent,
                                          'weekly_average_before', round(before / (p ->> 'baseline_weeks')::numeric, 1))) INTO v
        FROM (SELECT artist_id,
                     count(*) FILTER (WHERE played_at >= now() - make_interval(days => (p ->> 'days')::int)) AS recent,
                     count(*) FILTER (WHERE played_at < now() - make_interval(days => (p ->> 'days')::int)) AS before
                FROM user_plays
               WHERE user_id = p_user
                 AND played_at >= now() - make_interval(days => (p ->> 'days')::int + 7 * (p ->> 'baseline_weeks')::int)
               GROUP BY artist_id) s
       WHERE recent >= (p ->> 'min_plays')::int
         AND recent >= (p ->> 'ratio')::numeric * greatest(before / (p ->> 'baseline_weeks')::numeric, 1)
         AND EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                      WHERE e.artist_id = s.artist_id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL
                        AND a.registration_closes_at > now()
                        AND a.registration_opens_at < now() + make_interval(days => (p ->> 'lead_days')::int));
    ELSIF r.rule_key = 'burst_engagement' THEN
      -- Every save (liked track, playlist add, album save, follow) opens a window; the densest window per artist is
      -- the burst.
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'saves', burst, 'window_hours', (p ->> 'window_hours')::int,
                                          'window_start', window_start, 'all_saves', total,
                                          'kinds', jsonb_build_object('liked', liked, 'playlist', playlist, 'album', album, 'follow', follow),
                                          'after_signup', after_signup, 'fan_tickets', fan_tickets)) INTO v
        FROM (SELECT DISTINCT ON (w.artist_id) w.artist_id, w.burst, w.window_start, w.total, w.liked, w.playlist, w.album, w.follow,
                     w.window_start < u.created_at + make_interval(days => (p ->> 'signup_days')::int) AS after_signup,
                     EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                              WHERE e.artist_id = w.artist_id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL
                                AND a.registration_closes_at > now()
                                AND a.registration_opens_at < now() + make_interval(days => (p ->> 'lead_days')::int)) AS fan_tickets
                FROM (SELECT artist_id, saved_at AS window_start,
                             count(*) OVER win AS burst,
                             count(*) FILTER (WHERE kind = 'liked') OVER win AS liked,
                             count(*) FILTER (WHERE kind = 'playlist') OVER win AS playlist,
                             count(*) FILTER (WHERE kind = 'album') OVER win AS album,
                             count(*) FILTER (WHERE kind = 'follow') OVER win AS follow,
                             count(*) OVER (PARTITION BY artist_id) AS total
                        FROM (SELECT al.artist_id, l.saved_at, l.source AS kind
                                FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
                               WHERE l.user_id = p_user AND l.saved_at IS NOT NULL
                              UNION ALL
                              SELECT al.artist_id, s.saved_at, 'album'
                                FROM user_album_saves s JOIN albums al ON al.id = s.album_id
                               WHERE s.user_id = p_user AND s.saved_at IS NOT NULL
                              UNION ALL
                              SELECT g.artist_id, g.followed_since, 'follow'
                                FROM user_artist_signals g
                               WHERE g.user_id = p_user AND g.followed_since IS NOT NULL) saves
                      WINDOW win AS (PARTITION BY artist_id ORDER BY saved_at
                                     RANGE BETWEEN CURRENT ROW AND make_interval(hours => (p ->> 'window_hours')::int) FOLLOWING)) w
                JOIN users u ON u.id = p_user
               WHERE w.window_start >= now() - make_interval(days => (p ->> 'days')::int)
                 AND w.burst >= (p ->> 'min_saves')::int
                 AND w.burst >= (p ->> 'min_share')::numeric * w.total
               ORDER BY w.artist_id, w.burst DESC, w.window_start) b
       WHERE after_signup OR fan_tickets;
    END IF;
    -- A rule whose flag staff cleared recently stays quiet for its cooldown.
    IF v IS NOT NULL AND NOT EXISTS (
         SELECT 1 FROM fraud_flags f
          WHERE f.user_id = p_user AND f.source = 'rule' AND f.status = 'cleared'
            AND f.evidence -> 'rules' ? r.rule_key
            AND f.decided_at > now() - make_interval(days => COALESCE((p ->> 'cooldown_days')::int, 30))) THEN
      v_hits := v_hits || jsonb_build_object(r.rule_key, v);
    END IF;
  END LOOP;
  RETURN v_hits;
END $$;

-- Same as 0045, plus fans whose follow the sync first saw in the window (playlist adds are user_track_likes rows with a
-- saved_at now, so they were already covered).
CREATE OR REPLACE FUNCTION run_fraud_detection(p_since interval DEFAULT interval '1 day') RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT user_id FROM user_plays WHERE played_at >= now() - p_since
           UNION
           SELECT user_id FROM user_track_likes WHERE saved_at >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM user_album_saves WHERE saved_at >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM user_artist_signals WHERE followed_since >= now() - p_since - interval '1 day' LOOP
    IF detect_fraud(r.user_id) IS NOT NULL THEN
      v_count := v_count + 1;
    END IF;
  END LOOP;
  RETURN v_count;
END $$;
