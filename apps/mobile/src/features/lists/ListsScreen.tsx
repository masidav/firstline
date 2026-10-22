import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { ApiError, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { VISIBILITIES, type Visibility } from '../album-journal/journal';
import { type AlbumList, describeList } from './lists';

/** The fan's lists (GET /me/lists) and a form to start a new one (POST /lists). */
export function ListsScreen() {
  const { session } = useSession();
  const [lists, setLists] = useState<AlbumList[] | null>(null);
  const [error, setError] = useState<string>();
  const [title, setTitle] = useState('');
  const [visibility, setVisibility] = useState<Visibility>('public');
  const [creating, setCreating] = useState(false);
  const [formError, setFormError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setLists(unwrap(await session.api.GET('/me/lists', { params: { query: { limit: 100 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function create() {
    setCreating(true);
    setFormError(undefined);
    try {
      const list = unwrap(await session.api.POST('/lists', { body: { title: title.trim(), visibility } }));
      setTitle('');
      router.push(`/list/${list.id}`);
    } catch (e) {
      setFormError(e instanceof ApiError ? (e.fieldError('title') ?? errorMessage(e)) : errorMessage(e));
    } finally {
      setCreating(false);
    }
  }

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Your lists</Title>
      <ErrorText>{error}</ErrorText>
      {!lists && !error && <Loading />}
      {lists?.length === 0 && <Body muted>No lists yet. Rank your favourite records, or collect what to listen to next.</Body>}
      {lists?.map((list) => (
        <Pressable key={list.id} accessibilityRole="button" accessibilityLabel={`Open list ${list.title}`} onPress={() => router.push(`/list/${list.id}`)} style={styles.row}>
          <Text style={styles.title}>{list.title}</Text>
          <Text style={styles.note}>{describeList(list)}</Text>
        </Pressable>
      ))}

      <Text accessibilityRole="header" style={styles.heading}>
        New list
      </Text>
      <Field label="Title" value={title} onChangeText={setTitle} maxLength={120} autoCapitalize="sentences" error={formError} />
      <View style={styles.choices}>
        {VISIBILITIES.map((option) => (
          <Pressable
            key={option.value}
            accessibilityRole="button"
            accessibilityLabel={`Visibility ${option.label}`}
            accessibilityState={{ selected: visibility === option.value }}
            onPress={() => setVisibility(option.value)}
            style={[styles.choice, visibility === option.value && styles.selected]}
          >
            <Text style={[styles.choiceText, visibility === option.value && styles.selectedText]}>{option.label}</Text>
          </Pressable>
        ))}
      </View>
      <Button label="Create list" onPress={() => void create()} busy={creating} disabled={!title.trim()} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { padding: space.md, borderRadius: 12, backgroundColor: colors.surface, gap: 2 },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
  heading: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
  choices: { flexDirection: 'row', flexWrap: 'wrap', gap: space.xs },
  choice: { minWidth: 44, minHeight: 40, paddingHorizontal: space.sm, borderRadius: 8, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  selected: { backgroundColor: colors.accent, borderColor: colors.accent },
  choiceText: { color: colors.text, fontSize: 14 },
  selectedText: { color: colors.onAccent, fontWeight: '600' },
});
