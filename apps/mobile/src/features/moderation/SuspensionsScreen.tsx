import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { formatDate } from '../submissions/photoUpload';

type AdminSuspension = components['schemas']['AdminSuspension'];

/** Staff only (route /suspensions): paused accounts, those asking to be unbanned first, to lift or deny. */
export function SuspensionsScreen() {
  const { session } = useSession();
  const [items, setItems] = useState<AdminSuspension[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [replies, setReplies] = useState<Record<string, string>>({});

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setItems(unwrap(await session.api.GET('/admin/suspensions', { params: { query: { limit: 50 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function act(item: AdminSuspension, action: 'lift' | 'deny' | 'lift-now') {
    setBusy(item.id);
    setError(undefined);
    try {
      const reply = replies[item.id]?.trim();
      if (action === 'lift-now') {
        unwrap(await session.api.POST('/admin/users/{handle}/suspension/lift', { params: { path: { handle: item.user.handle ?? '' } } }));
        setItems((current) => current?.filter((i) => i.id !== item.id) ?? null);
      } else {
        const updated = unwrap(
          await session.api.POST('/admin/appeals/{appealId}/decision', {
            params: { path: { appealId: item.appeal!.id } },
            body: reply ? { decision: action, reply } : { decision: action },
          }),
        );
        setItems((current) => (action === 'lift' ? current?.filter((i) => i.id !== item.id) : current?.map((i) => (i.id === item.id ? updated : i))) ?? null);
      }
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!items && !error) return <Loading />;

  return (
    <Screen>
      <Title>Paused accounts</Title>
      <ErrorText>{error}</ErrorText>
      {items?.length === 0 && <Body muted>No accounts are paused.</Body>}
      {items?.map((item) => {
        const pending = item.appeal?.status === 'pending';
        return (
          <View key={item.id} style={styles.card}>
            <Text style={styles.title}>
              @{item.user.handle ?? 'deleted'} · {item.reason === 'bot' ? 'Flagged as a bot' : 'Paused'} · until {formatDate(item.ends_at)}
            </Text>
            {item.message ? <Text style={styles.text}>Told the fan: “{item.message}”</Text> : null}
            {item.note ? <Text style={styles.muted}>Staff note: {item.note}</Text> : null}
            {item.appeal && (
              <View style={styles.appeal}>
                <Text style={styles.label}>
                  {pending ? 'Asks to be unbanned' : `Request ${item.appeal.status}`} · {formatDate(item.appeal.submitted_at)}
                </Text>
                <Text style={styles.text}>“{item.appeal.message}”</Text>
                {item.appeal.reply ? <Text style={styles.muted}>Reply: {item.appeal.reply}</Text> : null}
              </View>
            )}
            {pending ? (
              <>
                <Field
                  label="Reply to the fan (needed to deny)"
                  value={replies[item.id] ?? ''}
                  onChangeText={(text) => setReplies((current) => ({ ...current, [item.id]: text }))}
                  maxLength={500}
                  autoCapitalize="sentences"
                />
                <Button label="Lift the pause" busy={busy === item.id} onPress={() => void act(item, 'lift')} />
                <Button
                  label="Deny request"
                  kind="danger"
                  busy={busy === item.id}
                  disabled={!replies[item.id]?.trim()}
                  onPress={() => void act(item, 'deny')}
                />
              </>
            ) : (
              <Button label="Lift the pause" kind="secondary" busy={busy === item.id} onPress={() => void act(item, 'lift-now')} />
            )}
          </View>
        );
      })}
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  text: { color: colors.text, fontSize: 15, lineHeight: 21 },
  muted: { color: colors.muted, fontSize: 14 },
  label: { color: colors.accent, fontSize: 14, fontWeight: '600' },
  appeal: { gap: space.xs, paddingLeft: space.sm, borderLeftWidth: 2, borderLeftColor: colors.accent },
});
