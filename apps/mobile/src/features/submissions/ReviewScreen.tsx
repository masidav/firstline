import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { kindLabel, type Submission, submissionConcertLine } from './photoUpload';

type AdminSubmission = components['schemas']['AdminSubmission'];

/** Staff only (route /review): posts waiting for review, oldest first, with approve and reject. */
export function ReviewScreen() {
  const { session } = useSession();
  const [queue, setQueue] = useState<AdminSubmission[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [reasons, setReasons] = useState<Record<string, string>>({});

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setQueue(unwrap(await session.api.GET('/admin/reviews', { params: { query: { limit: 50 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function decide(item: AdminSubmission, decision: 'approve' | 'reject') {
    setBusy(item.id);
    setError(undefined);
    try {
      unwrap(
        await session.api.POST('/admin/submissions/{submissionId}/decision', {
          params: { path: { submissionId: item.id } },
          body: decision === 'approve' ? { decision } : { decision, reason: reasons[item.id]?.trim() ?? '' },
        }),
      );
      setQueue((current) => current?.filter((q) => q.id !== item.id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!queue && !error) return <Loading />;

  return (
    <Screen>
      <Title>Review queue</Title>
      <ErrorText>{error}</ErrorText>
      {queue?.length === 0 && <Body muted>Nothing waiting.</Body>}
      {queue?.map((item) => (
        <View key={item.id} style={styles.card}>
          {item.files?.map((file) => (
            <Image key={file.id} source={{ uri: file.url }} style={styles.photo} resizeMode="contain" accessibilityLabel="Submitted photo" />
          ))}
          <Text style={styles.title}>
            {kindLabel(item.kind)} · {item.artist.name} · @{item.user?.handle ?? 'deleted'}
          </Text>
          {submissionConcertLine(item as Submission) ? (
            <Text style={styles.caption}>Concert: {submissionConcertLine(item as Submission)}</Text>
          ) : null}
          {item.caption ? <Text style={styles.caption}>{item.caption}</Text> : null}
          <Button label="Approve" busy={busy === item.id} onPress={() => void decide(item, 'approve')} />
          <Field
            label="Reason if rejecting (the fan sees it)"
            value={reasons[item.id] ?? ''}
            onChangeText={(text) => setReasons((current) => ({ ...current, [item.id]: text }))}
            maxLength={500}
            autoCapitalize="sentences"
          />
          <Button
            label="Reject"
            kind="danger"
            busy={busy === item.id}
            disabled={!reasons[item.id]?.trim()}
            onPress={() => void decide(item, 'reject')}
          />
        </View>
      ))}
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  caption: { color: colors.text, fontSize: 15 },
});
