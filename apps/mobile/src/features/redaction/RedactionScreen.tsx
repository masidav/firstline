import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { kindLabel } from '../submissions/photoUpload';

type RedactionReview = components['schemas']['RedactionReview'];

/** "in 3 days", "today", or "overdue": when the quarantine bucket deletes the original. */
export function deletesIn(deletedAfter: string, now: Date = new Date()): string {
  const days = Math.floor((new Date(deletedAfter).getTime() - now.getTime()) / 86_400_000);
  if (days < 0) return 'overdue';
  if (days === 0) return 'today';
  return days === 1 ? 'in 1 day' : `in ${days} days`;
}

/**
 * Staff only (route /redactions): verified proof the redaction worker couldn't settle. Look at the photo: if no barcode,
 * QR code, or order number is visible, archive it (the worker still blacks out any code it finds); otherwise discard it.
 */
export function RedactionScreen() {
  const { session } = useSession();
  const [items, setItems] = useState<RedactionReview[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setItems(unwrap(await session.api.GET('/admin/redactions')).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function decide(item: RedactionReview, action: 'archive' | 'discard') {
    setBusy(item.file_id);
    setError(undefined);
    try {
      unwrap(
        await session.api.POST('/admin/redactions/{fileId}/decision', {
          params: { path: { fileId: item.file_id } },
          body: { action },
        }),
      );
      setItems((current) => current?.filter((i) => i.file_id !== item.file_id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!items && !error) return <Loading />;

  return (
    <Screen>
      <Title>Photo redaction</Title>
      <Body muted>
        Barcodes and QR codes are blacked out before proof is archived. These photos need your eyes. Archive one only if
        no barcode, QR code, or order number is visible.
      </Body>
      <ErrorText>{error}</ErrorText>
      {items?.length === 0 && <Body muted>Nothing waiting.</Body>}
      {items?.map((item) => {
        const fan = item.user.deleted ? 'Deleted user' : `@${item.user.handle}`;
        return (
          <View key={item.file_id} style={styles.card}>
            <Text style={styles.title}>
              {kindLabel(item.kind)} · {item.artist.name} · {fan}
            </Text>
            <Image source={{ uri: item.file.url }} style={styles.photo} resizeMode="contain" accessibilityLabel="Photo to check" />
            {item.note ? <Text style={styles.note}>{item.note}</Text> : null}
            <Text style={styles.meta}>
              {item.codes_redacted > 0 ? `${item.codes_redacted} codes already blacked out. ` : ''}
              Original deleted {deletesIn(item.deleted_after)}.
            </Text>
            <Button label="Archive" busy={busy === item.file_id} onPress={() => void decide(item, 'archive')} />
            <Button label="Discard photo" kind="danger" busy={busy === item.file_id} onPress={() => void decide(item, 'discard')} />
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
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  note: { color: colors.danger, fontSize: 14, fontWeight: '600' },
  meta: { color: colors.muted, fontSize: 14 },
});
