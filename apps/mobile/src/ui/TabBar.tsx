import type { BottomTabBarProps } from 'expo-router/js-tabs';
import type { ReactNode } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, space } from './theme';

/**
 * The bottom tab bar: text labels (no icon font in the app), a count badge from `tabBarBadge`, per-tab label colors
 * from `tabBarActiveTintColor` and `tabBarInactiveTintColor` (the Profile tab turns red for unread alerts), and an
 * optional view above the tabs (the now-playing bar). Tabs are buttons named by their title, like the links they replace.
 */
export function TabBar({ state, descriptors, navigation, insets, above }: BottomTabBarProps & { above?: ReactNode }) {
  return (
    <View style={styles.wrap}>
      {above}
      <View style={[styles.bar, { paddingBottom: insets.bottom }]} accessibilityRole="tablist">
        {state.routes.map((route, index) => {
          const { options } = descriptors[route.key] ?? {};
          const focused = state.index === index;
          const title = options?.title ?? route.name;
          const badge = options?.tabBarBadge;
          const color = focused ? (options?.tabBarActiveTintColor ?? colors.text) : (options?.tabBarInactiveTintColor ?? colors.muted);
          const press = () => {
            const event = navigation.emit({ type: 'tabPress', target: route.key, canPreventDefault: true });
            if (!focused && !event.defaultPrevented) navigation.navigate(route.name, route.params);
          };
          return (
            <Pressable
              key={route.key}
              accessibilityRole="button"
              accessibilityLabel={options?.tabBarAccessibilityLabel ?? title}
              accessibilityState={{ selected: focused }}
              testID={options?.tabBarButtonTestID}
              onPress={press}
              onLongPress={() => navigation.emit({ type: 'tabLongPress', target: route.key })}
              style={({ pressed }) => [styles.tab, pressed && styles.pressed]}
            >
              <View style={styles.labelWrap}>
                <Text style={[styles.label, { color }]} numberOfLines={1}>
                  {title}
                </Text>
                {badge != null && badge !== 0 && (
                  <View style={styles.badge}>
                    <Text style={styles.badgeText}>{typeof badge === 'number' && badge > 99 ? '99+' : badge}</Text>
                  </View>
                )}
              </View>
              {focused && <View style={styles.indicator} />}
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { backgroundColor: colors.surface },
  bar: { flexDirection: 'row', borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border, backgroundColor: colors.surface },
  tab: { flex: 1, minHeight: 52, alignItems: 'center', justifyContent: 'center', paddingHorizontal: space.xs },
  pressed: { opacity: 0.6 },
  labelWrap: { alignItems: 'center', justifyContent: 'center' },
  label: { fontSize: 13, fontWeight: '600' },
  badge: {
    position: 'absolute', top: -10, right: -18, minWidth: 18, height: 18, borderRadius: 9, paddingHorizontal: 5,
    backgroundColor: colors.danger, alignItems: 'center', justifyContent: 'center',
  },
  badgeText: { color: colors.onAccent, fontSize: 11, fontWeight: '700' },
  indicator: { position: 'absolute', bottom: 0, left: '20%', right: '20%', height: 2, borderRadius: 1, backgroundColor: colors.accent },
});
