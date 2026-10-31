import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { AppState, Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { colors, space } from '../../ui/theme';
import { ActionButton } from './ActionButton';
import { AFTER_COMMAND_MS, IDLE_POLL_MS, isActive, opensInSpotify, playerMessage, POLL_MS, progressAt, queueMessage, type SpotifyPlayer } from './player';

interface NowPlaying {
  player: SpotifyPlayer | null;
  /** When the player state was fetched (ms since epoch), for counting progress on. */
  fetchedAt: number;
  message: string | null;
  /** Plays a song on the fan's Spotify, or opens it in Spotify when that can't work. */
  play: (spotifyTrackId: string, spotifyUrl: string) => Promise<void>;
  /** Plays an album from its first track, or opens it in Spotify when that can't work. */
  playAlbum: (spotifyAlbumId: string, spotifyUrl: string) => Promise<void>;
  /** Adds a song after what's playing. Only offered while Spotify is playing or paused. */
  queue: (spotifyTrackId: string) => Promise<void>;
  toggle: () => Promise<void>;
  skip: (forward: boolean) => Promise<void>;
  /** Shows a line in the bar for a few seconds (other Spotify buttons report through it too). */
  notify: (message: string) => void;
  dismiss: () => void;
}

// Without a provider (tests, screens outside the app shell) a play just opens the song in Spotify, as before.
const fallback: NowPlaying = {
  player: null,
  fetchedAt: 0,
  message: null,
  play: async (_id, url) => {
    await Linking.openURL(url);
  },
  playAlbum: async (_id, url) => {
    await Linking.openURL(url);
  },
  queue: async () => {},
  toggle: async () => {},
  skip: async () => {},
  notify: () => {},
  dismiss: () => {},
};

const NowPlayingContext = createContext<NowPlaying>(fallback);

export function usePlayer(): NowPlaying {
  return useContext(NowPlayingContext);
}

/**
 * Mounted once under SessionProvider: asks the API what the fan's Spotify is playing while the app is open (never
 * stored), and runs play, pause, and skip. Spotify plays on the fan's phone or computer app, not in this app.
 */
export function NowPlayingProvider({ children }: { children: ReactNode }) {
  const { session, state } = useSession();
  const signedIn = state.status === 'signedIn';
  const [player, setPlayer] = useState<SpotifyPlayer | null>(null);
  const [fetchedAt, setFetchedAt] = useState(0);
  const [message, setMessage] = useState<string | null>(null);
  const [appActive, setAppActive] = useState(AppState.currentState !== 'background');
  const playerRef = useRef(player);
  playerRef.current = player;

  const refresh = useCallback(async () => {
    try {
      const next = unwrap(await session.api.GET('/me/spotify/player'));
      setPlayer(next);
      setFetchedAt(Date.now());
    } catch {
      // Spotify or the network is down: keep showing the last state until the next poll.
    }
  }, [session]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next) => setAppActive(next === 'active'));
    return () => subscription.remove();
  }, []);

  // Slower polling while there's nothing Spotify could show; re-armed only when that changes.
  const idle = player?.status === 'not_connected' || player?.status === 'reconnect_needed';
  useEffect(() => {
    if (!signedIn) {
      setPlayer(null);
      return;
    }
    if (!appActive) return;
    void refresh();
    const timer = setInterval(() => void refresh(), idle ? IDLE_POLL_MS : POLL_MS);
    return () => clearInterval(timer);
  }, [signedIn, appActive, refresh, idle]);

  useEffect(() => {
    if (!message) return;
    const timer = setTimeout(() => setMessage(null), 6000);
    return () => clearTimeout(timer);
  }, [message]);

  const command = useCallback(
    async (run: () => Promise<unknown>) => {
      try {
        await run();
        setMessage(null);
      } catch (error) {
        setMessage(playerMessage(error));
      }
      setTimeout(() => void refresh(), AFTER_COMMAND_MS);
    },
    [refresh],
  );

  const start = useCallback(
    async (body: { spotify_track_id?: string; spotify_album_id?: string }, spotifyUrl: string) => {
      const status = playerRef.current?.status;
      if (status === 'not_connected' || status === 'reconnect_needed') {
        await Linking.openURL(spotifyUrl);
        return;
      }
      try {
        unwrap(await session.api.PUT('/me/spotify/player/play', { body }));
        setMessage(null);
      } catch (error) {
        setMessage(playerMessage(error));
        if (opensInSpotify(error)) await Linking.openURL(spotifyUrl);
      }
      setTimeout(() => void refresh(), AFTER_COMMAND_MS);
    },
    [session, refresh],
  );

  const play = useCallback((spotifyTrackId: string, spotifyUrl: string) => start({ spotify_track_id: spotifyTrackId }, spotifyUrl), [start]);
  const playAlbum = useCallback((spotifyAlbumId: string, spotifyUrl: string) => start({ spotify_album_id: spotifyAlbumId }, spotifyUrl), [start]);

  const queue = useCallback(
    async (spotifyTrackId: string) => {
      try {
        unwrap(await session.api.POST('/me/spotify/player/queue', { body: { spotify_track_id: spotifyTrackId } }));
        setMessage('Added to your Spotify queue.');
      } catch (error) {
        setMessage(queueMessage(error));
      }
    },
    [session],
  );

  const toggle = useCallback(
    () =>
      command(async () => {
        if (playerRef.current?.status === 'playing') {
          unwrap(await session.api.PUT('/me/spotify/player/pause'));
          setPlayer((current) => (current ? { ...current, status: 'paused' } : current));
        } else {
          unwrap(await session.api.PUT('/me/spotify/player/play', { body: {} }));
          setPlayer((current) => (current ? { ...current, status: 'playing' } : current));
        }
        setFetchedAt(Date.now());
      }),
    [command, session],
  );

  const skip = useCallback(
    (forward: boolean) =>
      command(async () => {
        unwrap(await (forward ? session.api.POST('/me/spotify/player/next') : session.api.POST('/me/spotify/player/previous')));
      }),
    [command, session],
  );

  const value = useMemo<NowPlaying>(
    () => ({ player, fetchedAt, message, play, playAlbum, queue, toggle, skip, notify: setMessage, dismiss: () => setMessage(null) }),
    [player, fetchedAt, message, play, playAlbum, queue, toggle, skip],
  );
  return <NowPlayingContext.Provider value={value}>{children}</NowPlayingContext.Provider>;
}

/**
 * The bar along the bottom of every screen while the fan's Spotify is playing or paused. On the tabs it sits above the
 * tab bar, which pads for the bottom inset itself (`insetBottom={false}`).
 */
export function NowPlayingBar({ insetBottom = true }: { insetBottom?: boolean } = {}) {
  const { player, fetchedAt, message, toggle, skip, dismiss } = usePlayer();
  const insets = useSafeAreaInsets();
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (player?.status !== 'playing') return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [player?.status]);

  const track = isActive(player) ? player?.track : null;
  if (!track && !message) return null;

  const progress = progressAt(player, Math.max(0, now - fetchedAt));
  const share = track?.duration_ms && progress != null ? Math.min(1, progress / track.duration_ms) : 0;
  const playing = player?.status === 'playing';
  const artists = track?.artists.map((a) => a.name).join(', ') ?? '';

  return (
    <View style={[styles.bar, { paddingBottom: space.sm + (insetBottom ? insets.bottom : 0) }]} accessibilityLabel="Now playing on Spotify">
      {track && (
        <>
          <View style={styles.progressTrack}>
            <View style={[styles.progressFill, { width: `${Math.round(share * 100)}%` }]} />
          </View>
          <View style={styles.row}>
            <Pressable
              accessibilityRole="link"
              accessibilityLabel={`Open ${track.title} on Spotify`}
              onPress={() => void Linking.openURL(track.spotify_url)}
              style={styles.song}
            >
              {track.cover_url ? (
                <Image source={{ uri: track.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors />
              ) : (
                <View style={styles.cover} />
              )}
              <View style={styles.text}>
                <Text style={styles.title} numberOfLines={1}>
                  {track.title}
                </Text>
                <Text style={styles.muted} numberOfLines={1}>
                  {artists}
                  {player?.device ? ` · on ${player.device.name}` : ''}
                </Text>
                <Text style={styles.spotify}>Spotify</Text>
              </View>
            </Pressable>
            <Control label="⏮" accessibilityLabel="Previous song" onPress={() => void skip(false)} />
            <Control label={playing ? '❚❚' : '▶'} accessibilityLabel={playing ? 'Pause' : 'Play'} onPress={() => void toggle()} big />
            <Control label="⏭" accessibilityLabel="Next song" onPress={() => void skip(true)} />
          </View>
        </>
      )}
      {message && (
        <Pressable accessibilityRole="button" accessibilityLabel={`${message} Dismiss`} onPress={dismiss}>
          <Text style={styles.message}>{message}</Text>
        </Pressable>
      )}
    </View>
  );
}

function Control({ label, accessibilityLabel, onPress, big }: { label: string; accessibilityLabel: string; onPress: () => void; big?: boolean }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel} onPress={onPress} hitSlop={8}
      style={({ pressed }) => [styles.control, big && styles.controlBig, pressed && styles.pressed]}>
      <Text style={[styles.controlLabel, big && styles.controlLabelBig]}>{label}</Text>
    </Pressable>
  );
}

export type SongProps = { spotifyTrackId: string; spotifyUrl: string; title: string; compact?: boolean };

/** Plays a song on the fan's Spotify; pauses it when it's the one playing. Sits beside "Open in Spotify" links. */
export function PlayButton({ spotifyTrackId, spotifyUrl, title, compact }: SongProps) {
  const { player, play, toggle } = usePlayer();
  const [busy, setBusy] = useState(false);
  const current = player?.track?.spotify_track_id === spotifyTrackId && isActive(player);
  const playing = current && player?.status === 'playing';

  const press = async () => {
    setBusy(true);
    try {
      if (current) await toggle();
      else await play(spotifyTrackId, spotifyUrl);
    } finally {
      setBusy(false);
    }
  };

  const label = compact ? (playing ? '❚❚' : '▶') : playing ? '❚❚ Pause' : current ? '▶ Resume' : '▶ Play';
  return (
    <ActionButton accessibilityLabel={playing ? `Pause ${title}` : `Play ${title}`} label={label} busy={busy} onPress={press} compact={compact} primary />
  );
}

/** Plays an album from its first track. Beside the album's Spotify link on the album screen. */
export function PlayAlbumButton({ spotifyAlbumId, spotifyUrl, title }: { spotifyAlbumId: string; spotifyUrl: string; title: string }) {
  const { playAlbum } = usePlayer();
  const [busy, setBusy] = useState(false);
  const press = async () => {
    setBusy(true);
    try {
      await playAlbum(spotifyAlbumId, spotifyUrl);
    } finally {
      setBusy(false);
    }
  };
  return <ActionButton accessibilityLabel={`Play ${title}`} label="▶ Play album" busy={busy} onPress={press} primary />;
}

/** Adds a song after what's playing. Shown only while the fan's Spotify is playing or paused another song. */
export function QueueButton({ spotifyTrackId, title, compact }: Omit<SongProps, 'spotifyUrl'>) {
  const { player, queue } = usePlayer();
  const [busy, setBusy] = useState(false);
  if (!isActive(player) || player?.track?.spotify_track_id === spotifyTrackId) return null;

  const press = async () => {
    setBusy(true);
    try {
      await queue(spotifyTrackId);
    } finally {
      setBusy(false);
    }
  };
  return <ActionButton accessibilityLabel={`Add ${title} to the queue`} label={compact ? '+' : '+ Queue'} busy={busy} onPress={press} compact={compact} />;
}

const styles = StyleSheet.create({
  bar: { backgroundColor: colors.surface, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border, paddingHorizontal: space.md, paddingTop: space.sm, gap: space.xs },
  progressTrack: { height: 2, borderRadius: 1, backgroundColor: colors.border, overflow: 'hidden' },
  progressFill: { height: 2, backgroundColor: colors.accent },
  row: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  song: { flex: 1, flexDirection: 'row', alignItems: 'center', gap: space.sm },
  cover: { width: 44, height: 44, borderRadius: 4, backgroundColor: colors.border },
  text: { flex: 1 },
  title: { color: colors.text, fontSize: 15, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 13 },
  spotify: { color: '#1ed760', fontSize: 11, fontWeight: '700' },
  control: { width: 36, height: 36, borderRadius: 18, alignItems: 'center', justifyContent: 'center' },
  controlBig: { width: 44, height: 44, borderRadius: 22, backgroundColor: colors.text },
  controlLabel: { color: colors.text, fontSize: 16 },
  controlLabelBig: { color: colors.background, fontSize: 16, fontWeight: '700' },
  pressed: { opacity: 0.6 },
  message: { color: colors.muted, fontSize: 13, paddingVertical: space.xs },
});
