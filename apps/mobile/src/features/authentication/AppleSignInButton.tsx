import { Pressable, StyleSheet, Text } from 'react-native';

/**
 * Web (and Android, where it is never shown): a Continue with Apple button in Apple's style, black with white text,
 * the same size and corners as Apple's own button on iOS (AppleSignInButton.native.tsx).
 */
export function AppleSignInButton({ onPress, disabled }: { onPress: () => void; disabled?: boolean }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel="Continue with Apple"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.button, (pressed || disabled) && styles.dimmed]}
    >
      <Text style={styles.label}>Continue with Apple</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: { width: '100%', height: 48, borderRadius: 12, backgroundColor: '#000000', alignItems: 'center', justifyContent: 'center' },
  label: { color: '#ffffff', fontSize: 16, fontWeight: '600' },
  dimmed: { opacity: 0.6 },
});
