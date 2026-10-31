import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';

import { type components, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { libraryMessage } from './player';
import { ActionButton } from './ActionButton';
import { usePlayer } from './NowPlaying';

export type SpotifyLibrary = components['schemas']['SpotifyLibrary'];
export type LibraryStatus = SpotifyLibrary['status'] | 'unknown';
export type LibraryKind = 'track' | 'album';

/** Spotify's limits per request. */
export const MAX_TRACK_IDS = 50;
export const MAX_ALBUM_IDS = 20;
/** Buttons mounting in the same frame share one request. */
export const BATCH_MS = 50;

interface Library {
  status: LibraryStatus;
  /** Whether the song or album is in the fan's Spotify library; undefined until known. */
  isSaved: (kind: LibraryKind, id: string) => boolean | undefined;
  /** Asks Spotify about this id (batched with others); returns nothing to clean up, the answer is kept. */
  watch: (kind: LibraryKind, id: string) => void;
  /** Saves or removes it, telling the bar when that didn't work. */
  setSaved: (kind: LibraryKind, id: string, saved: boolean) => Promise<void>;
}

// Without a provider (tests, screens outside the app shell) there are no Like buttons.
const fallback: Library = { status: 'not_connected', isSaved: () => undefined, watch: () => {}, setSaved: async () => {} };

const LibraryContext = createContext<Library>(fallback);

export function useLibrary(): Library {
  return useContext(LibraryContext);
}

const key = (kind: LibraryKind, id: string) => `${kind}:${id}`;

/**
 * Mounted once under NowPlayingProvider: knows which songs and albums on screen are in the fan's Spotify library
 * (read on request, never stored) and saves or removes them when a Like button is pressed.
 */
export function SpotifyLibraryProvider({ children }: { children: ReactNode }) {
  const { session, state } = useSession();
  const signedIn = state.status === 'signedIn';
  const { notify } = usePlayer();
  const [status, setStatus] = useState<LibraryStatus>('unknown');
  const [saved, setSaved] = useState<Record<string, boolean>>({});
  const statusRef = useRef(status);
  statusRef.current = status;
  const known = useRef(new Set<string>());
  const pending = useRef(new Set<string>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (signedIn) return;
    known.current.clear();
    pending.current.clear();
    setSaved({});
    setStatus('unknown');
  }, [signedIn]);

  const flush = useCallback(async () => {
    timer.current = null;
    // Up to Spotify's limits per request; anything over waits for the next batch.
    const take = (kind: LibraryKind, max: number) => {
      const ids = [...pending.current].filter((k) => k.startsWith(`${kind}:`)).slice(0, max);
      ids.forEach((k) => pending.current.delete(k));
      return ids.map((k) => k.slice(kind.length + 1));
    };
    const tracks = take('track', MAX_TRACK_IDS);
    const albums = take('album', MAX_ALBUM_IDS);
    const keys = [...tracks.map((id) => key('track', id)), ...albums.map((id) => key('album', id))];
    if (keys.length === 0) return;
    try {
      const library = unwrap(
        await session.api.GET('/me/spotify/library', {
          params: { query: { track_ids: tracks.join(',') || undefined, album_ids: albums.join(',') || undefined } },
        }),
      );
      setStatus(library.status);
      if (library.status === 'ok') {
        setSaved((current) => {
          const next = { ...current };
          library.tracks.forEach((t) => (next[key('track', t.spotify_track_id)] = t.saved));
          library.albums.forEach((a) => (next[key('album', a.spotify_album_id)] = a.saved));
          return next;
        });
      }
    } catch {
      // Spotify or the network is down: the buttons stay in their last state; a later screen asks again.
      keys.forEach((k) => known.current.delete(k));
    }
    if (pending.current.size > 0 && !timer.current) timer.current = setTimeout(() => void flush(), BATCH_MS);
  }, [session]);

  const watch = useCallback(
    (kind: LibraryKind, id: string) => {
      if (!signedIn || statusRef.current === 'not_connected' || statusRef.current === 'reconnect_needed') return;
      const k = key(kind, id);
      if (known.current.has(k)) return;
      known.current.add(k);
      pending.current.add(k);
      if (!timer.current) timer.current = setTimeout(() => void flush(), BATCH_MS);
    },
    [signedIn, flush],
  );

  const change = useCallback(
    async (kind: LibraryKind, id: string, next: boolean) => {
      const k = key(kind, id);
      const before = saved[k];
      setSaved((current) => ({ ...current, [k]: next }));
      try {
        if (kind === 'track') {
          unwrap(
            await (next
              ? session.api.PUT('/me/spotify/library/tracks/{spotifyTrackId}', { params: { path: { spotifyTrackId: id } } })
              : session.api.DELETE('/me/spotify/library/tracks/{spotifyTrackId}', { params: { path: { spotifyTrackId: id } } })),
          );
        } else {
          unwrap(
            await (next
              ? session.api.PUT('/me/spotify/library/albums/{spotifyAlbumId}', { params: { path: { spotifyAlbumId: id } } })
              : session.api.DELETE('/me/spotify/library/albums/{spotifyAlbumId}', { params: { path: { spotifyAlbumId: id } } })),
          );
        }
      } catch (error) {
        setSaved((current) => (before === undefined ? omit(current, k) : { ...current, [k]: before }));
        notify(libraryMessage(error));
      }
    },
    [session, saved, notify],
  );

  const value = useMemo<Library>(
    () => ({ status, isSaved: (kind, id) => saved[key(kind, id)], watch, setSaved: change }),
    [status, saved, watch, change],
  );
  return <LibraryContext.Provider value={value}>{children}</LibraryContext.Provider>;
}

function omit(record: Record<string, boolean>, k: string): Record<string, boolean> {
  const { [k]: _, ...rest } = record;
  return rest;
}

/**
 * Saves a song or album to the fan's Spotify library, or removes it. Hidden without a Spotify connection; with a
 * connection made before the library permission existed, pressing it asks the fan to connect again.
 */
export function LikeButton({ kind, id, title, compact }: { kind: LibraryKind; id: string; title: string; compact?: boolean }) {
  const { status, isSaved, watch, setSaved } = useLibrary();
  const { notify } = usePlayer();
  const [busy, setBusy] = useState(false);
  useEffect(() => watch(kind, id), [watch, kind, id]);
  if (status === 'not_connected') return null;

  const saved = isSaved(kind, id) === true;
  const press = async () => {
    if (status === 'reconnect_needed') {
      notify(`Connect Spotify again in your profile to like ${kind === 'track' ? 'songs' : 'albums'} here.`);
      return;
    }
    setBusy(true);
    try {
      await setSaved(kind, id, !saved);
    } finally {
      setBusy(false);
    }
  };
  const label = compact ? (saved ? '♥' : '♡') : saved ? '♥ Liked' : '♡ Like';
  return <ActionButton accessibilityLabel={saved ? `Unlike ${title}` : `Like ${title}`} label={label} busy={busy} onPress={press} compact={compact} active={saved} />;
}
