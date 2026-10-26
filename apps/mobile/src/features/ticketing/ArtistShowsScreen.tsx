import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { saleLines, statusLabel, whenLabel } from './showInfo';

type Show = components['schemas']['Event'];

/** An artist's upcoming shows (GET /artists/{id}/events). Shows with fan tickets say so. */
export function ArtistShowsScreen({ artistId }: { artistId: string }) {
  const { session } = useSession();
  const [shows, setShows] = useState<Show[] | null>(null);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const page = unwrap(
        await session.api.GET('/artists/{artistId}/events', { params: { path: { artistId }, query: { limit: 100 } } }),
      );
      setShows(page.items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, artistId]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Upcoming shows</Title>
      {error && (
        <>
          <ErrorText>{error}</ErrorText>
          <Button label="Try again" kind="secondary" onPress={() => void load()} />
        </>
      )}
      {!shows && !error && <Loading />}
      {shows?.length === 0 && <Body muted>No upcoming shows listed yet.</Body>}
      {shows?.map((show) => (
        <Pressable
          key={show.id}
          accessibilityRole="button"
          accessibilityLabel={`Open ${show.name}`}
          onPress={() => router.push(`/show/${show.id}`)}
          style={styles.row}
        >
          <View style={styles.rowText}>
            <Text style={styles.name}>{show.name}</Text>
            <Text style={styles.note}>
              {whenLabel(show)} · {[show.venue, show.city].filter(Boolean).join(', ')}
            </Text>
            {saleLines(show)[0] ? <Text style={styles.note}>{saleLines(show)[0]}</Text> : null}
          </View>
          {show.tickets_available ? (
            <Text style={styles.badge}>Fan tickets</Text>
          ) : statusLabel(show.status) && show.status !== 'onsale' ? (
            <Text style={[styles.badge, show.status === 'cancelled' && styles.cancelled]}>{statusLabel(show.status)}</Text>
          ) : null}
        </Pressable>
      ))}
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.md, paddingVertical: space.sm },
  rowText: { flex: 1, gap: 2 },
  name: { color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
  badge: { color: colors.accent, fontSize: 14, fontWeight: '600' },
  cancelled: { color: colors.danger },
});
