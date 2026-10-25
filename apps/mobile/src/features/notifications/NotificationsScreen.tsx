import { type Href, router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { publishUnread } from './unread';

export type Notification = components['schemas']['Notification'];

/** "5 min ago", "3 h ago", "2 d ago", else the date. */
export function ago(iso: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60_000));
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 60 * 24) return `${Math.round(minutes / 60)} h ago`;
  if (minutes < 60 * 24 * 7) return `${Math.round(minutes / 60 / 24)} d ago`;
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

/**
 * Notifications (GET /me/notifications): the draw's outcome, a seat that opened up, a delivered ticket; for staff,
 * draws, reports, and proof to review. Tapping one marks it read and opens its screen; "Mark all read" clears the badge.
 */
export function NotificationsScreen() {
  const { session } = useSession();
  const [items, setItems] = useState<Notification[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const load = useCallback(
    async (after?: string) => {
      setError(undefined);
      try {
        const page = unwrap(await session.api.GET('/me/notifications', { params: { query: { limit: 30, cursor: after } } }));
        setItems((current) => (after ? [...(current ?? []), ...page.items] : page.items));
        setCursor(page.next_cursor ?? null);
        publishUnread(page.unread_count);
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session],
  );

  useEffect(() => {
    void load();
  }, [load]);

  async function markRead(ids?: string[]) {
    const result = unwrap(await session.api.POST('/me/notifications/read', { body: ids ? { ids } : {} }));
    publishUnread(result.unread_count);
    setItems((current) => current && current.map((n) => (!ids || ids.includes(n.id) ? { ...n, read: true } : n)));
  }

  async function open(notification: Notification) {
    try {
      if (!notification.read) await markRead([notification.id]);
    } catch {
      // Opening matters more than the read mark.
    }
    router.push(notification.link as Href);
  }

  async function markAll() {
    setBusy(true);
    setError(undefined);
    try {
      await markRead();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const unread = items?.some((n) => !n.read) ?? false;
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Alerts</Title>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {!items && !error && <Loading />}
      {items?.length === 0 && <Body muted>Nothing yet. You'll hear here when a fan draw picks you or your ticket arrives.</Body>}
      {unread && <Button label="Mark all read" kind="secondary" busy={busy} onPress={() => void markAll()} />}
      {items?.map((notification) => (
        <Pressable
          key={notification.id}
          accessibilityRole="button"
          accessibilityLabel={`${notification.read ? '' : 'Unread: '}${notification.title}. ${notification.body}`}
          onPress={() => void open(notification)}
          style={({ pressed }) => [styles.row, !notification.read && styles.unread, pressed && styles.pressed]}
        >
          <View style={styles.titleRow}>
            {!notification.read && <View style={styles.dot} />}
            <Text style={styles.title}>{notification.title}</Text>
            <Text style={styles.when}>{ago(notification.created_at)}</Text>
          </View>
          <Text style={styles.body}>{notification.body}</Text>
        </Pressable>
      ))}
      {cursor && <Button label="Show more" kind="secondary" onPress={() => void load(cursor)} />}
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { gap: space.xs, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  unread: { borderColor: colors.accent, backgroundColor: colors.surface },
  pressed: { opacity: 0.7 },
  titleRow: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: colors.accent },
  title: { flex: 1, color: colors.text, fontSize: 16, fontWeight: '700' },
  when: { color: colors.muted, fontSize: 13 },
  body: { color: colors.text, fontSize: 15, lineHeight: 21 },
});
