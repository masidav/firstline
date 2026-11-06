import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { ApiError, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { config } from '../../config';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { connectAppleMusic } from './appleMusicConnect';
import {
  connectErrorMessage,
  describeStatus,
  isSyncing,
  MUSIC_PROVIDERS,
  type MusicProvider,
  type MusicStatus,
  notConnected,
  PROVIDER_NAMES,
} from './musicConnect';
import { startTidalConnect } from './tidalRedirect';

const POLL_MS = 3000;

type Statuses = Record<MusicProvider, MusicStatus>;
type Action = 'connect' | 'sync' | 'disconnect';

/** Whether this build has what the provider's connect flow needs (src/config.ts). */
export function providerConfigured(provider: MusicProvider): boolean {
  return provider === 'apple_music' ? config.appleMusicEnabled : Boolean(config.tidalClientId);
}

const CONFIG_HINT: Record<MusicProvider, string> = {
  apple_music: 'EXPO_PUBLIC_APPLE_MUSIC_ENABLED',
  tidal: 'EXPO_PUBLIC_TIDAL_CLIENT_ID',
};

/** Connect, sync, and disconnect Apple Music and Tidal (openapi /me/music). Spotify has its own screen. */
export function MusicScreen() {
  const { session } = useSession();
  const [statuses, setStatuses] = useState<Statuses | null>(null);
  const [loadError, setLoadError] = useState<string>();
  const [errors, setErrors] = useState<Partial<Record<MusicProvider, string>>>({});
  const [busy, setBusy] = useState<{ provider: MusicProvider; action: Action } | null>(null);
  const [confirming, setConfirming] = useState<MusicProvider | null>(null);

  const show = useCallback((next: MusicStatus) => {
    setStatuses((current) => ({ ...(current ?? emptyStatuses()), [next.provider]: next }));
  }, []);

  const setError = useCallback((provider: MusicProvider, message?: string) => {
    setErrors((current) => ({ ...current, [provider]: message }));
  }, []);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { items } = unwrap(await session.api.GET('/me/music'));
        if (cancelled) return;
        const next = emptyStatuses();
        for (const item of items) next[item.provider] = item;
        setStatuses(next);
      } catch (e) {
        if (!cancelled) setLoadError(connectErrorMessage(e));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [session]);

  const refresh = useCallback(
    async (provider: MusicProvider) => {
      try {
        show(unwrap(await session.api.GET('/me/music/{provider}', { params: { path: { provider } } })));
      } catch (e) {
        setError(provider, connectErrorMessage(e, provider));
      }
    },
    [session, show, setError],
  );

  // While a provider's sync is queued or running, check it again every few seconds.
  useEffect(() => {
    const syncing = MUSIC_PROVIDERS.filter((provider) => isSyncing(statuses?.[provider]));
    if (syncing.length === 0) return;
    const timer = setTimeout(() => syncing.forEach((provider) => void refresh(provider)), POLL_MS);
    return () => clearTimeout(timer);
  }, [statuses, refresh]);

  async function connect(provider: MusicProvider) {
    setBusy({ provider, action: 'connect' });
    setError(provider, undefined);
    try {
      const outcome =
        provider === 'tidal'
          ? await startTidalConnect(config.tidalClientId, async (body) =>
              unwrap(await session.api.PUT('/me/music/{provider}', { params: { path: { provider: 'tidal' } }, body })),
            )
          : await connectAppleMusic(session.api);
      if (outcome === 'redirecting') return; // the page is leaving for Tidal
      setBusy(null);
      if (outcome === 'cancelled') return;
      if (outcome.ok) show(outcome.status);
      else setError(provider, outcome.message);
    } catch (e) {
      setError(provider, e instanceof Error ? e.message : String(e));
      setBusy(null);
    }
  }

  async function sync(provider: MusicProvider) {
    setBusy({ provider, action: 'sync' });
    setError(provider, undefined);
    try {
      show(unwrap(await session.api.POST('/me/music/{provider}/sync', { params: { path: { provider } } })));
    } catch (e) {
      setError(
        provider,
        e instanceof ApiError && e.code === 'rate_limited'
          ? 'You can sync once every 15 minutes. Your library also syncs automatically every day.'
          : connectErrorMessage(e, provider),
      );
    } finally {
      setBusy(null);
    }
  }

  async function disconnect(provider: MusicProvider) {
    setBusy({ provider, action: 'disconnect' });
    setError(provider, undefined);
    try {
      unwrap(await session.api.DELETE('/me/music/{provider}', { params: { path: { provider } } }));
      show(notConnected(provider));
      setConfirming(null);
    } catch (e) {
      setError(provider, connectErrorMessage(e, provider));
    } finally {
      setBusy(null);
    }
  }

  if (!statuses && !loadError) {
    return <Loading />;
  }

  return (
    <Screen>
      <Title>Music services</Title>
      <Body muted>
        Connect the services you listen on. We read your library, playlists, and what you play most to measure fandom
        for tracked artists; nothing about other artists is stored, and we never change anything in your library.
      </Body>
      <ErrorText>{loadError}</ErrorText>

      {statuses &&
        MUSIC_PROVIDERS.map((provider) => {
          const name = PROVIDER_NAMES[provider];
          const status = statuses[provider];
          const needsConnect = !status.connected || status.sync_state === 'reauth_required';
          const isBusy = (action: Action) => busy?.provider === provider && busy.action === action;
          return (
            <View key={provider} style={styles.section}>
              <Text accessibilityRole="header" style={styles.heading}>
                {name}
              </Text>
              <Body>{describeStatus(status)}</Body>
              <ErrorText>{errors[provider]}</ErrorText>

              {!providerConfigured(provider) ? (
                <Body muted>
                  {name} isn't set up for this build ({CONFIG_HINT[provider]}).
                </Body>
              ) : (
                needsConnect && (
                  <Button
                    label={status.connected ? 'Connect again' : `Connect ${name}`}
                    accessibilityLabel={status.connected ? `Connect ${name} again` : `Connect ${name}`}
                    onPress={() => void connect(provider)}
                    busy={isBusy('connect')}
                  />
                )
              )}

              {status.connected && !needsConnect && (
                <Button
                  label={isSyncing(status) ? 'Syncing…' : 'Sync now'}
                  accessibilityLabel={isSyncing(status) ? `Syncing ${name}` : `Sync ${name} now`}
                  onPress={() => void sync(provider)}
                  busy={isBusy('sync')}
                  disabled={isSyncing(status)}
                />
              )}

              {status.connected &&
                (confirming === provider ? (
                  <>
                    <Body>
                      Disconnect {name}? The library data synced from {name} is deleted; anything synced from your other
                      services stays.
                    </Body>
                    <Button
                      label="Yes, disconnect"
                      accessibilityLabel={`Yes, disconnect ${name}`}
                      kind="danger"
                      onPress={() => void disconnect(provider)}
                      busy={isBusy('disconnect')}
                    />
                    <Button
                      label="Keep connected"
                      accessibilityLabel={`Keep ${name} connected`}
                      kind="secondary"
                      onPress={() => setConfirming(null)}
                    />
                  </>
                ) : (
                  <Button label={`Disconnect ${name}`} kind="secondary" onPress={() => setConfirming(provider)} />
                ))}
            </View>
          );
        })}

      <View style={{ height: 16 }} />
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

function emptyStatuses(): Statuses {
  return { apple_music: notConnected('apple_music'), tidal: notConnected('tidal') };
}

const styles = StyleSheet.create({
  section: { gap: space.md, marginTop: space.sm },
  heading: { color: colors.text, fontSize: 20, fontWeight: '700' },
});
