import { Pressable, StyleSheet, Text } from 'react-native';

import { colors } from '../../ui/theme';

/** A pill button for the Spotify actions (Play, Queue, Like); `primary` for Play, outlined for the rest. */
export function ActionButton({
  label,
  accessibilityLabel,
  onPress,
  busy,
  compact,
  primary,
  active,
}: {
  label: string;
  accessibilityLabel: string;
  onPress: () => Promise<void> | void;
  busy?: boolean;
  /** Icon only, for a row of tracks. */
  compact?: boolean;
  primary?: boolean;
  /** Pressed-in look, for a song that's already liked. */
  active?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ busy: !!busy, selected: !!active }}
      disabled={busy}
      onPress={() => void onPress()}
      hitSlop={8}
      style={({ pressed }) => [
        styles.button,
        !primary && styles.secondaryButton,
        active && styles.activeButton,
        compact && styles.compactButton,
        (pressed || busy) && styles.pressed,
      ]}
    >
      <Text style={[styles.label, !primary && styles.secondaryLabel, active && styles.activeLabel]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: { alignSelf: 'flex-start', paddingVertical: 6, paddingHorizontal: 14, borderRadius: 16, backgroundColor: colors.accent },
  secondaryButton: { backgroundColor: 'transparent', borderWidth: 1, borderColor: colors.border },
  activeButton: { borderColor: colors.accent },
  compactButton: { paddingHorizontal: 10, minWidth: 36, alignItems: 'center' },
  pressed: { opacity: 0.6 },
  label: { color: colors.onAccent, fontSize: 14, fontWeight: '700' },
  secondaryLabel: { color: colors.text },
  activeLabel: { color: colors.accent },
});
