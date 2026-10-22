import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { CommentsSection } from '../comments/CommentsSection';
import { AlbumRow } from './AlbumRow';
import { type AlbumLog, formatRating } from './journal';

/** One log (GET /logs/{id}) with its comment thread. */
export function LogScreen({ logId }: { logId: string }) {
  const { session } = useSession();
  const [log, setLog] = useState<AlbumLog | null>(null);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setLog(unwrap(await session.api.GET('/logs/{logId}', { params: { path: { logId } } })));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, logId]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      {error && <ErrorText>{error}</ErrorText>}
      {!log && !error && <Loading />}
      {log && (
        <>
          <Title>{log.user.deleted ? 'Deleted user' : `@${log.user.handle}`}</Title>
          <AlbumRow
            albumId={log.album.id}
            title={log.album.title}
            coverUrl={log.album.cover_url}
            note={[log.album.artist?.name, log.listened_on, log.is_relisten ? 'relisten' : null].filter(Boolean).join(' · ')}
            trailing={formatRating(log.rating)}
          />
          {log.review ? (
            <View style={styles.review}>
              <Body>{log.review}</Body>
            </View>
          ) : null}
          <CommentsSection logId={log.id} />
        </>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  review: { padding: space.md, borderRadius: 12, backgroundColor: colors.surface },
});
