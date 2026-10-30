-- Fraud rule for "click everything" (Phase 10, 2026-10-30; docs/security/gaming-strategies.md strategy 4): like every
-- track and save every album of an artist in one sitting, right after signing up or while the artist's fan tickets
-- are in sight. It's free, instant, and until now undetected.
--
-- What the database keeps: user_track_likes (source 'liked') and user_album_saves are replaced on every sync, but
-- their saved_at is Spotify's own added_at, so the time of each save survives the snapshot and a burst is visible
-- without a sync history. Playlist tracks (source 'playlist') carry no saved_at and the artist follow is a boolean, so
-- neither counts here. Only catalog items of tracked artists are ever stored.
--
-- burst_engagement (other): for one artist, at least min_saves saves (liked tracks + album saves) inside any
-- window_hours window that started in the last days days, holding at least min_share of all the fan's saves of that
-- artist (a long-time saver bingeing a new release isn't a burst), and either the window started within signup_days
-- of the account's creation, or the artist has fan tickets open or opening within lead_days. Starting values are
-- provisional (PROJECT_TODO.md).
--
-- The sweep now also checks fans with saves in the window (saves reach us with the daily sync, so it looks a day
-- further back than for plays): a clicker needn't play anything. A flag names the strongest reason that fired:
-- bot, then streaming_farm, then the rest (before, rules were ordered by key after bot, which would have let this
-- rule name a looping fan's flag 'other').

INSERT INTO fraud_rules (rule_key, reason, params, description) VALUES
  ('burst_engagement', 'other',
   '{"days": 14, "window_hours": 24, "min_saves": 40, "min_share": 0.8, "signup_days": 7, "lead_days": 14, "cooldown_days": 30}',
   'At least min_saves liked tracks and album saves of one artist within window_hours, min_share of all the fan''s saves of the artist, within signup_days of sign-up or while the artist has fan tickets open or opening within lead_days');

CREATE INDEX user_track_likes_saved_idx ON user_track_likes (saved_at) WHERE saved_at IS NOT NULL;
CREATE INDEX user_album_saves_saved_idx ON user_album_saves (saved_at) WHERE saved_at IS NOT NULL;

-- Same as 0043, with burst_engagement.
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
      -- Every save opens a window; the densest window per artist is the burst.
      SELECT jsonb_agg(jsonb_build_object('artist_id', artist_id, 'saves', burst, 'window_hours', (p ->> 'window_hours')::int,
                                          'window_start', window_start, 'all_saves', total,
                                          'after_signup', after_signup, 'fan_tickets', fan_tickets)) INTO v
        FROM (SELECT DISTINCT ON (w.artist_id) w.artist_id, w.burst, w.window_start, w.total,
                     w.window_start < u.created_at + make_interval(days => (p ->> 'signup_days')::int) AS after_signup,
                     EXISTS (SELECT 1 FROM allocations a JOIN events e ON e.id = a.event_id
                              WHERE e.artist_id = w.artist_id AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL
                                AND a.registration_closes_at > now()
                                AND a.registration_opens_at < now() + make_interval(days => (p ->> 'lead_days')::int)) AS fan_tickets
                FROM (SELECT artist_id, saved_at AS window_start,
                             count(*) OVER (PARTITION BY artist_id ORDER BY saved_at
                                            RANGE BETWEEN CURRENT ROW AND make_interval(hours => (p ->> 'window_hours')::int) FOLLOWING) AS burst,
                             count(*) OVER (PARTITION BY artist_id) AS total
                        FROM (SELECT al.artist_id, l.saved_at
                                FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
                               WHERE l.user_id = p_user AND l.saved_at IS NOT NULL
                              UNION ALL
                              SELECT al.artist_id, s.saved_at
                                FROM user_album_saves s JOIN albums al ON al.id = s.album_id
                               WHERE s.user_id = p_user AND s.saved_at IS NOT NULL) saves) w
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

-- Same as 0043, except the flag is named by the strongest reason that fired: bot, then streaming_farm, then the rest.
CREATE OR REPLACE FUNCTION detect_fraud(p_user uuid) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_hits jsonb; v_reason text; v_id uuid;
BEGIN
  PERFORM 1 FROM users WHERE id = p_user AND deleted_at IS NULL AND NOT is_staff FOR NO KEY UPDATE;
  IF NOT FOUND OR fan_on_hold(p_user) THEN RETURN NULL; END IF;
  v_hits := fraud_rule_hits(p_user);
  IF v_hits = '{}' THEN RETURN NULL; END IF;
  SELECT reason INTO v_reason FROM fraud_rules WHERE v_hits ? rule_key
   ORDER BY reason = 'bot' DESC, reason = 'streaming_farm' DESC, rule_key LIMIT 1;
  INSERT INTO fraud_flags (user_id, reason, evidence, source)
  VALUES (p_user, v_reason, jsonb_build_object('rules', v_hits, 'checked_at', now()), 'rule')
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Same as 0043, plus fans with saves in the window. Saves arrive with the daily sync, so for them the sweep looks a
-- day further back than p_since; checking a fan twice is harmless (a held fan is skipped).
CREATE OR REPLACE FUNCTION run_fraud_detection(p_since interval DEFAULT interval '1 day') RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; v_count integer := 0;
BEGIN
  FOR r IN SELECT user_id FROM user_plays WHERE played_at >= now() - p_since
           UNION
           SELECT user_id FROM user_track_likes WHERE saved_at >= now() - p_since - interval '1 day'
           UNION
           SELECT user_id FROM user_album_saves WHERE saved_at >= now() - p_since - interval '1 day' LOOP
    IF detect_fraud(r.user_id) IS NOT NULL THEN
      v_count := v_count + 1;
    END IF;
  END LOOP;
  RETURN v_count;
END $$;
