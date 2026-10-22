import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { AlbumRow } from './AlbumRow';
import { type AlbumLog, formatRating } from './journal';
import { outbox, type PendingLog } from './outbox';

/**
 * The fan's diary (GET /me/logs), newest listen first, with delete. Tap an entry to open the album and edit.
 * Logs saved offline are sent first (outbox) and shown at the top until they get through.
 */
export function JournalScreen() {
  const { session } = useSession();
  const [logs, setLogs] = useState<AlbumLog[] | null>(null);
  const [pending, setPending] = useState<PendingLog[]>(() => outbox().list());
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(
    async (cursor?: string) => {
      setError(undefined);
      try {
        if (!cursor && outbox().list().some((entry) => !entry.failure)) {
          await outbox().flush(session.api);
          setPending(outbox().list());
        }
        const page = unwrap(await session.api.GET('/me/logs', { params: { query: { limit: 50, ...(cursor ? { cursor } : {}) } } }));
        setLogs((current) => (cursor && current ? [...current, ...page.items] : page.items));
        setNextCursor(page.next_cursor);
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session],
  );

  useEffect(() => {
    void load();
  }, [load]);

  async function remove(log: AlbumLog) {
    setDeleting(log.id);
    setError(undefined);
    try {
      unwrap(await session.api.DELETE('/logs/{logId}', { params: { path: { logId: log.id } } }));
      setLogs((current) => current?.filter((item) => item.id !== log.id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setDeleting(null);
    }
  }

  return (
    <Screen>
      <View style={styles.actions}>
        <Button label="Back to home" kind="secondary" onPress={() => router.replace('/home')} />
        <Button label="Your lists" kind="secondary" onPress={() => router.push('/lists')} />
      </View>
      <Title>Your journal</Title>
      <ErrorText>{error}</ErrorText>
      {pending.map((entry) => (
        <View key={entry.key} style={styles.pending}>
          <Body>{entry.albumTitle}</Body>
          <Body muted>{entry.failure ? `Not saved: ${entry.failure}` : 'Waiting to send'}</Body>
          <View style={styles.actions}>
            {!entry.failure && <Button label="Send now" kind="secondary" onPress={() => void load()} />}
            <Button
              label="Discard"
              accessibilityLabel={`Discard offline log of ${entry.albumTitle}`}
              kind="danger"
              onPress={() => {
                outbox().remove(entry.key);
                setPending(outbox().list());
              }}
            />
          </View>
        </View>
      ))}
      {!logs && !error && <Loading />}
      {logs?.length === 0 && <Body muted>Nothing logged yet. Open an album from one of your artists and log it.</Body>}
      {logs?.map((log) => (
        <View key={log.id} style={styles.entry}>
          <AlbumRow
            albumId={log.album.id}
            title={log.album.title}
            coverUrl={log.album.cover_url}
            note={[log.album.artist?.name, log.listened_on, log.is_relisten ? 'relisten' : null].filter(Boolean).join(' · ')}
            trailing={formatRating(log.rating)}
          />
          {log.review ? (
            <Body muted>{log.review.length > 160 ? `${log.review.slice(0, 160)}…` : log.review}</Body>
          ) : null}
          <View style={styles.actions}>
            <Button
              label="Delete"
              accessibilityLabel={`Delete log of ${log.album.title}`}
              kind="danger"
              onPress={() => void remove(log)}
              busy={deleting === log.id}
              disabled={deleting !== null}
            />
          </View>
        </View>
      ))}
      {nextCursor && <Button label="Show more" kind="secondary" onPress={() => void load(nextCursor)} />}
    </Screen>
  );
}

const styles = StyleSheet.create({
  entry: { gap: space.sm },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm, justifyContent: 'flex-end' },
  pending: { gap: space.xs, padding: space.md, borderRadius: 12, backgroundColor: colors.surface },
});
