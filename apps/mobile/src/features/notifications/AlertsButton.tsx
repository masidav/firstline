import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { colors, space } from '../../ui/theme';
import { publishUnread, subscribeUnread } from './unread';

const POLL_MS = 60_000;

/** "Alerts" with the unread count (GET /me/notifications, polled every minute and updated when notifications are read). */
export function AlertsButton() {
  const { session } = useSession();
  const [unread, setUnread] = useState(0);

  const load = useCallback(async () => {
    try {
      const page = unwrap(await session.api.GET('/me/notifications', { params: { query: { limit: 1 } } }));
      publishUnread(page.unread_count ?? 0);
    } catch {
      // The badge is a hint; the screen itself shows errors.
    }
  }, [session]);

  useEffect(() => subscribeUnread(setUnread), []);
  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), POLL_MS);
    return () => clearInterval(timer);
  }, [load]);

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={unread > 0 ? `Alerts, ${unread} unread` : 'Alerts'}
      onPress={() => router.push('/notifications')}
      style={({ pressed }) => [styles.button, pressed && styles.pressed]}
    >
      <Text style={styles.label}>Alerts</Text>
      {unread > 0 && (
        <View style={styles.badge}>
          <Text style={styles.badgeText}>{unread > 99 ? '99+' : unread}</Text>
        </View>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, paddingHorizontal: space.md,
    flexDirection: 'row', alignItems: 'center', gap: space.xs,
  },
  pressed: { opacity: 0.6 },
  label: { color: colors.text, fontSize: 16, fontWeight: '600' },
  badge: { minWidth: 22, height: 22, borderRadius: 11, paddingHorizontal: 6, backgroundColor: colors.danger, alignItems: 'center', justifyContent: 'center' },
  badgeText: { color: colors.onAccent, fontSize: 12, fontWeight: '700' },
});
