import { router } from 'expo-router';
import { useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { ApiError, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { space } from '../../ui/theme';
import type { AlbumList } from './lists';

/** "Add to list" on an album: picks one of the fan's lists (GET /me/lists) and appends the album. */
export function AddToList({ albumId, albumTitle }: { albumId: string; albumTitle: string }) {
  const { session } = useSession();
  const [lists, setLists] = useState<AlbumList[] | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string>();
  const [error, setError] = useState<string>();

  async function show() {
    setOpen(true);
    setNotice(undefined);
    setError(undefined);
    try {
      setLists(unwrap(await session.api.GET('/me/lists', { params: { query: { limit: 100 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  async function add(list: AlbumList) {
    setBusy(list.id);
    setError(undefined);
    try {
      unwrap(await session.api.POST('/lists/{listId}/items', { params: { path: { listId: list.id } }, body: { album_id: albumId } }));
      setNotice(`Added ${albumTitle} to ${list.title}.`);
      setOpen(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setNotice(`${albumTitle} is already on ${list.title}.`);
        setOpen(false);
      } else {
        setError(errorMessage(e));
      }
    } finally {
      setBusy(null);
    }
  }

  return (
    <View style={styles.box}>
      {!open && <Button label="Add to list" kind="secondary" onPress={() => void show()} />}
      {open && lists?.length === 0 && (
        <>
          <Body muted>You have no lists yet.</Body>
          <Button label="Make one" kind="secondary" onPress={() => router.push('/lists')} />
        </>
      )}
      {open &&
        lists?.map((list) => (
          <Button key={list.id} label={list.title} accessibilityLabel={`Add to ${list.title}`} kind="secondary" onPress={() => void add(list)} busy={busy === list.id} disabled={busy !== null} />
        ))}
      {open && <Button label="Cancel" kind="secondary" onPress={() => setOpen(false)} disabled={busy !== null} />}
      {notice && <Body>{notice}</Body>}
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

const styles = StyleSheet.create({
  box: { gap: space.sm },
});
