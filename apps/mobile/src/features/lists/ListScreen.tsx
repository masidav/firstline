import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { AlbumRow } from '../album-journal/AlbumRow';
import { SpotifyAttribution } from '../album-journal/SpotifyAttribution';
import { type AlbumListDetail, describeList } from './lists';

/**
 * One list (GET /lists/{id}) in order. The owner can move items up and down (PATCH position), remove them,
 * and delete the list; everyone else reads.
 */
export function ListScreen({ listId }: { listId: string }) {
  const { session, state } = useSession();
  const myHandle = state.status === 'signedIn' ? state.me.handle : undefined;
  const [list, setList] = useState<AlbumListDetail | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setList(unwrap(await session.api.GET('/lists/{listId}', { params: { path: { listId } } })));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, listId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function change(action: () => Promise<unknown>) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const move = (albumId: string, position: number) =>
    change(async () => unwrap(await session.api.PATCH('/lists/{listId}/items/{albumId}', { params: { path: { listId, albumId } }, body: { position } })));
  const remove = (albumId: string) =>
    change(async () => unwrap(await session.api.DELETE('/lists/{listId}/items/{albumId}', { params: { path: { listId, albumId } } })));
  const deleteList = () =>
    change(async () => {
      unwrap(await session.api.DELETE('/lists/{listId}', { params: { path: { listId } } }));
      router.replace('/lists');
    });

  if (!list) {
    return (
      <Screen>
        <Button label="Back" kind="secondary" onPress={back} />
        <ErrorText>{error}</ErrorText>
        {!error && <Loading />}
      </Screen>
    );
  }

  const mine = list.user.handle === myHandle;
  const items = list.items ?? [];
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={back} />
      <Title>{list.title}</Title>
      <Body muted>
        {mine ? 'Your list' : list.user.deleted ? 'By a deleted user' : `By @${list.user.handle}`} · {describeList(list)}
      </Body>
      {list.description ? <Body>{list.description}</Body> : null}
      <ErrorText>{error}</ErrorText>
      {items.length === 0 && <Body muted>Nothing here yet. Open an album and choose "Add to list".</Body>}
      {items.map((item, index) => (
        <View key={item.album.id} style={styles.item}>
          <Text style={styles.position}>{item.position}</Text>
          <View style={styles.itemBody}>
            <AlbumRow albumId={item.album.id} title={item.album.title} coverUrl={item.album.cover_url} note={item.album.artist?.name} />
            {item.note ? <Body muted>{item.note}</Body> : null}
            {mine && (
              <View style={styles.actions}>
                <Button label="Up" accessibilityLabel={`Move ${item.album.title} up`} kind="secondary" onPress={() => void move(item.album.id, item.position - 1)} disabled={busy || index === 0} />
                <Button label="Down" accessibilityLabel={`Move ${item.album.title} down`} kind="secondary" onPress={() => void move(item.album.id, item.position + 1)} disabled={busy || index === items.length - 1} />
                <Button label="Remove" accessibilityLabel={`Remove ${item.album.title}`} kind="danger" onPress={() => void remove(item.album.id)} disabled={busy} />
              </View>
            )}
          </View>
        </View>
      ))}
      {items.length > 0 && <SpotifyAttribution />}
      {mine && !confirmingDelete && <Button label="Delete list" kind="secondary" onPress={() => setConfirmingDelete(true)} disabled={busy} />}
      {mine && confirmingDelete && (
        <View style={styles.actions}>
          <Button label="Yes, delete it" kind="danger" onPress={() => void deleteList()} busy={busy} />
          <Button label="Keep it" kind="secondary" onPress={() => setConfirmingDelete(false)} disabled={busy} />
        </View>
      )}
    </Screen>
  );
}

function back() {
  if (router.canGoBack()) router.back();
  else router.replace('/lists');
}

const styles = StyleSheet.create({
  item: { flexDirection: 'row', gap: space.sm, alignItems: 'flex-start' },
  position: { color: colors.muted, fontSize: 16, fontWeight: '700', width: 24, paddingTop: space.md },
  itemBody: { flex: 1, gap: space.xs },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
});
