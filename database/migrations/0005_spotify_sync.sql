-- Spotify sync state (openapi SpotifyStatus) and the function that queues a sync.
-- See docs/adr/0005-spotify-integration.md.

-- sync_state: idle (nothing pending), queued, running, failed (gave up; the schedule tries again later),
-- reauth_required (Spotify rejected the refresh token; only reconnecting clears it).
ALTER TABLE spotify_connections
  ADD COLUMN sync_state            text NOT NULL DEFAULT 'idle'
    CONSTRAINT spotify_connections_sync_state
    CHECK (sync_state IN ('idle', 'queued', 'running', 'failed', 'reauth_required')),
  ADD COLUMN sync_error            text                -- stable code, never provider text or tokens
    CONSTRAINT spotify_connections_sync_error
    CHECK (sync_error IN ('spotify_unavailable', 'spotify_forbidden', 'spotify_reauth_required', 'internal_error')),
  ADD COLUMN sync_requested_at     timestamptz,         -- last manual request (rate limit)
  ADD COLUMN last_sync_attempt_at  timestamptz,         -- last time a sync started (schedule)
  ADD COLUMN tracked_artists_found integer NOT NULL DEFAULT 0 CHECK (tracked_artists_found >= 0);

CREATE INDEX spotify_connections_due_idx ON spotify_connections (last_sync_attempt_at NULLS FIRST)
  WHERE sync_state IN ('idle', 'failed');

-- Marks the connection queued and enqueues its sync job, at most one queued per user. Call inside the
-- transaction that needs the sync. Returns false when the user has no Spotify connection.
CREATE FUNCTION request_spotify_sync(p_user uuid) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
  UPDATE spotify_connections SET sync_state = 'queued', sync_error = NULL WHERE user_id = p_user;
  IF NOT FOUND THEN RETURN false; END IF;
  PERFORM enqueue_job('spotify.sync', jsonb_build_object('user_id', p_user), 'spotify.sync:' || p_user);
  RETURN true;
END $$;
