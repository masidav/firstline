import { router } from 'expo-router';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, space } from '../../ui/theme';
import { useUnreadCount } from './unread';

/** "Alerts" on the Profile page, with the unread count; red while something is unread (the tabs layout polls the count). */
export function AlertsButton() {
  const unread = useUnreadCount(false);

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={unread > 0 ? `Alerts, ${unread} unread` : 'Alerts'}
      onPress={() => router.push('/notifications')}
      style={({ pressed }) => [styles.button, unread > 0 && styles.unread, pressed && styles.pressed]}
    >
      <Text style={[styles.label, unread > 0 && styles.labelUnread]}>Alerts</Text>
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
  unread: { borderColor: colors.danger },
  label: { color: colors.text, fontSize: 16, fontWeight: '600' },
  labelUnread: { color: colors.danger },
  badge: { minWidth: 22, height: 22, borderRadius: 11, paddingHorizontal: 6, backgroundColor: colors.danger, alignItems: 'center', justifyContent: 'center' },
  badgeText: { color: colors.onAccent, fontSize: 12, fontWeight: '700' },
});
