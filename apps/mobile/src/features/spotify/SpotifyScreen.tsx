import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { View } from 'react-native';

import { ApiError, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { config } from '../../config';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { connectErrorMessage, describeStatus, isSyncing, type SpotifyStatus } from './spotifyConnect';
import { startSpotifyConnect } from './spotifyRedirect';

const POLL_MS = 3000;

/** Connect, sync, and disconnect Spotify (openapi /me/spotify). */
export function SpotifyScreen() {
  const { session } = useSession();
  const [status, setStatus] = useState<SpotifyStatus | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'connect' | 'sync' | 'disconnect' | null>(null);
  const [confirmingDisconnect, setConfirmingDisconnect] = useState(false);

  const show = useCallback(
    (next: SpotifyStatus) => {
      setStatus(next);
      session.updateMe({ spotify: next });
    },
    [session],
  );

  const load = useCallback(async () => {
    try {
      show(unwrap(await session.api.GET('/me/spotify')));
    } catch (e) {
      setError(connectErrorMessage(e));
    }
  }, [session, show]);

  useEffect(() => {
    void load();
  }, [load]);

  // While a sync is queued or running, check again every few seconds.
  useEffect(() => {
    if (!isSyncing(status)) return;
    const timer = setTimeout(() => void load(), POLL_MS);
    return () => clearTimeout(timer);
  }, [status, load]);

  async function connect() {
    setBusy('connect');
    setError(undefined);
    try {
      await startSpotifyConnect(config.spotifyClientId);
      // The page is now leaving for Spotify.
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(null);
    }
  }

  async function sync() {
    setBusy('sync');
    setError(undefined);
    try {
      show(unwrap(await session.api.POST('/me/spotify/sync')));
    } catch (e) {
      setError(
        e instanceof ApiError && e.code === 'rate_limited'
          ? 'You can sync once every 15 minutes. Your library also syncs automatically every day.'
          : connectErrorMessage(e),
      );
    } finally {
      setBusy(null);
    }
  }

  async function disconnect() {
    setBusy('disconnect');
    setError(undefined);
    try {
      unwrap(await session.api.DELETE('/me/spotify'));
      show({ connected: false, sync_state: 'idle' });
      setConfirmingDisconnect(false);
    } catch (e) {
      setError(connectErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!status && !error) {
    return <Loading />;
  }

  const connectLabel = status?.connected ? 'Connect again' : 'Connect Spotify';
  const needsConnect = !status?.connected || status.sync_state === 'reauth_required';

  return (
    <Screen>
      <Title>Spotify</Title>
      <Body muted>
        We read your saved tracks, top artists, recent plays, and your own playlists to measure how much you listen
        to the artists we track. Nothing about other artists is stored. We never post or change anything.
      </Body>

      {status && <Body>{describeStatus(status)}</Body>}
      <ErrorText>{error}</ErrorText>

      {!config.spotifyClientId ? (
        <Body muted>Spotify isn't set up for this build (EXPO_PUBLIC_SPOTIFY_CLIENT_ID).</Body>
      ) : (
        needsConnect && <Button label={connectLabel} onPress={() => void connect()} busy={busy === 'connect'} />
      )}

      {status?.connected && !needsConnect && (
        <Button
          label={isSyncing(status) ? 'Syncing…' : 'Sync now'}
          onPress={() => void sync()}
          busy={busy === 'sync'}
          disabled={isSyncing(status)}
        />
      )}

      {status?.connected &&
        (confirmingDisconnect ? (
          <>
            <Body>
              Disconnect Spotify? Your synced library data is deleted. You can also remove Fandom Score from your
              Spotify account at spotify.com/account/apps.
            </Body>
            <Button label="Yes, disconnect" kind="danger" onPress={() => void disconnect()} busy={busy === 'disconnect'} />
            <Button label="Keep connected" kind="secondary" onPress={() => setConfirmingDisconnect(false)} />
          </>
        ) : (
          <Button label="Disconnect Spotify" kind="secondary" onPress={() => setConfirmingDisconnect(true)} />
        ))}

      <View style={{ height: 16 }} />
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}
