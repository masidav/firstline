import { useEffect } from 'react';

import { type Api, unwrap } from '../../api/client';
import type { SpotifyStatus } from './spotifyConnect';

let synced = false;

/** For tests: forget that this launch already synced. */
export function resetBackgroundSync(): void {
  synced = false;
}

/**
 * Asks for a Spotify sync once per app launch, in the background (POST /me/spotify/sync?background=true, decided
 * 2026-10-25). The server queues one only if the last sync is over 15 minutes old, and never answers with an error
 * for timing, so this stays silent; the returned status updates the session.
 */
export async function syncOnOpen(api: Api, connected: boolean, update: (status: SpotifyStatus) => void): Promise<void> {
  if (synced || !connected) return;
  synced = true;
  try {
    update(unwrap(await api.POST('/me/spotify/sync', { params: { query: { background: true } } })));
  } catch {
    // Offline, or Spotify isn't set up: the daily sync and the profile's Resync button still work.
  }
}

/** Runs syncOnOpen when a signed-in screen first appears. */
export function useSyncOnOpen(api: Api, connected: boolean, update: (status: SpotifyStatus) => void): void {
  useEffect(() => {
    void syncOnOpen(api, connected, update);
  }, [api, connected, update]);
}
