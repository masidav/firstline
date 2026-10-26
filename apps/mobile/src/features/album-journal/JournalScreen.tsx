import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { space } from '../../ui/theme';
import { AlbumRow } from './AlbumRow';
import { type AlbumLog, formatRating } from './journal';
import { journalQueue, usePendingLogs, useSyncedLogs } from './journalQueue';
import { PendingLogCard } from './PendingLogCard';

/** The fan's diary (GET /me/logs), newest listen first, with delete. Tap an entry to open the album and edit. */
export function JournalScreen() {
  const { session } = useSession();
  const [logs, setLogs] = useState<AlbumLog[] | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [deleting, setDeleting] = useState<string | null>(null);
  const pending = usePendingLogs();

  const load = useCallback(
    async (cursor?: string) => {
      setError(undefined);
      try {
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

  // An entry that was waiting is now a real log: show it in its place.
  useSyncedLogs(useCallback(() => void load(), [load]));

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
      <Button label="Back to home" kind="secondary" onPress={() => router.replace('/home')} />
      <Title>Your journal</Title>
      <ErrorText>{error}</ErrorText>
      {pending.length > 0 && (
        <View style={styles.pending}>
          <Body>
            {pending.length === 1 ? '1 entry waiting to sync' : `${pending.length} entries waiting to sync`}
          </Body>
          {pending.map((entry) => (
            <PendingLogCard key={entry.localId} entry={entry} showAlbum />
          ))}
          <Button label="Retry now" kind="secondary" onPress={() => void journalQueue.retry()} />
        </View>
      )}
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
  pending: { gap: space.sm, marginBottom: space.sm },
  actions: { alignItems: 'flex-end' },
});
