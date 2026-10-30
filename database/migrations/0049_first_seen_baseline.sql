-- Undated items a fan already had don't count as a burst (Phase 10, decided 2026-10-30; docs/security/gaming-strategies.md
-- strategy 4).
--
-- 0048 dates a playlist item Spotify leaves undated, and every follow, when the sync first sees it. On the fan's first
-- sync (or the first after 0048) every such item they already had gets the same stamp, so a long-time fan with a big,
-- very old playlist looked like a burst to burst_engagement and was held from registering until staff cleared the flag.
-- Decided: undated items already there when the sync first looked have an unknown date and never count toward a burst;
-- only undated items that appear between two syncs, and items Spotify dates, do.
--
-- - user_track_likes.saved_at_baseline: a playlist row Spotify left undated, already there when first seen; its saved_at
--   is the sync's stamp, not when it was added. user_artist_signals.followed_since_baseline: the same for the follow.
-- - The sync sets them on insert, for an artist whose items the fan's previous sync could not have seen: the fan's first
--   sync (no last_synced_at), or an artist that became active after it (newly tracked, often from the fan's own top
--   artists). A row Spotify dates, and a liked row, is never baseline.
-- - The flag is fixed for the life of the row (the triggers keep it on every update), so a re-sync can't turn a baseline
--   row countable or the other way round. A follow that ends and comes back, or a track taken out of every playlist and
--   put back, is a new row and counts.
-- - Backfill: everything stamped as first seen so far is baseline (every dated follow; every playlist row still undated,
--   which the next sync stamps; and every playlist row whose date has sub-second digits: Spotify's added_at is whole
--   seconds, a stamp is the sync's now()). Playlist rows with Spotify's own added_at stay countable.
-- - burst_engagement skips baseline rows when it looks for the burst; they still count among all the fan's saves of
--   the artist, which the burst must be min_share of.

ALTER TABLE user_track_likes ADD COLUMN saved_at_baseline boolean NOT NULL DEFAULT false;
ALTER TABLE user_artist_signals ADD COLUMN followed_since_baseline boolean NOT NULL DEFAULT false;

-- Before the triggers below, which keep the flag on update.
UPDATE user_track_likes SET saved_at_baseline = true
 WHERE source = 'playlist' AND (saved_at IS NULL OR saved_at <> date_trunc('second', saved_at));
UPDATE user_artist_signals SET followed_since_baseline = true WHERE followed_since IS NOT NULL;

-- Same as 0048, plus the baseline flag: set on insert only for an undated playlist row, kept on every update.
CREATE OR REPLACE FUNCTION trg_user_track_likes_first_seen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    NEW.saved_at_baseline := OLD.saved_at_baseline;
  ELSE
    NEW.saved_at_baseline := COALESCE(NEW.saved_at_baseline, false) AND NEW.source = 'playlist' AND NEW.saved_at IS NULL;
  END IF;
  IF NEW.source = 'playlist' AND NEW.saved_at IS NULL THEN
    NEW.saved_at := CASE WHEN TG_OP = 'UPDATE' THEN COALESCE(OLD.saved_at, now()) ELSE now() END;
  END IF;
  RETURN NEW;
END $$;

-- Same as 0048, plus the baseline flag: taken from the writer when the follow first appears, kept while it lasts,
-- cleared when it ends.
CREATE OR REPLACE FUNCTION trg_user_artist_signals_followed_since() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT NEW.followed THEN
    NEW.followed_since := NULL;
    NEW.followed_since_baseline := false;
  ELSIF TG_OP = 'UPDATE' AND OLD.followed AND OLD.followed_since IS NOT NULL THEN
    NEW.followed_since := OLD.followed_since;      -- still followed: the first sighting stands
    NEW.followed_since_baseline := OLD.followed_since_baseline;
  ELSE
    NEW.followed_since := COALESCE(NEW.followed_since, now());
    NEW.followed_since_baseline := COALESCE(NEW.followed_since_baseline, false);
  END IF;
  RETURN NEW;
END $$;

-- Same as 0048, except burst_engagement doesn't start or count a burst with baseline rows (they stay in all_saves).
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
      -- Every countable save (liked track, playlist add, album save, follow; not a baseline row, whose date is only
      -- when the sync first looked) opens a window; the densest window per artist is the burst. all_saves counts
      -- baseline rows too: they are the fan's saves of the artist all the same.
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
                FROM (SELECT artist_id, saved_at AS window_start, countable,
                             count(*) FILTER (WHERE countable) OVER win AS burst,
                             count(*) FILTER (WHERE countable AND kind = 'liked') OVER win AS liked,
                             count(*) FILTER (WHERE countable AND kind = 'playlist') OVER win AS playlist,
                             count(*) FILTER (WHERE countable AND kind = 'album') OVER win AS album,
                             count(*) FILTER (WHERE countable AND kind = 'follow') OVER win AS follow,
                             count(*) OVER (PARTITION BY artist_id) AS total
                        FROM (SELECT al.artist_id, l.saved_at, l.source AS kind, NOT l.saved_at_baseline AS countable
                                FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
                               WHERE l.user_id = p_user AND l.saved_at IS NOT NULL
                              UNION ALL
                              SELECT al.artist_id, s.saved_at, 'album', true
                                FROM user_album_saves s JOIN albums al ON al.id = s.album_id
                               WHERE s.user_id = p_user AND s.saved_at IS NOT NULL
                              UNION ALL
                              SELECT g.artist_id, g.followed_since, 'follow', NOT g.followed_since_baseline
                                FROM user_artist_signals g
                               WHERE g.user_id = p_user AND g.followed_since IS NOT NULL) saves
                      WINDOW win AS (PARTITION BY artist_id ORDER BY saved_at
                                     RANGE BETWEEN CURRENT ROW AND make_interval(hours => (p ->> 'window_hours')::int) FOLLOWING)) w
                JOIN users u ON u.id = p_user
               WHERE w.countable
                 AND w.window_start >= now() - make_interval(days => (p ->> 'days')::int)
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
