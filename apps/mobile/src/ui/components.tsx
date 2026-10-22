import type { ReactNode } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, type TextInputProps, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { colors, space } from './theme';

/** A centered, scrollable page column, comfortable on a phone and not stretched on a desktop browser. */
export function Screen({ children }: { children: ReactNode }) {
  return (
    <SafeAreaView style={styles.safe}>
      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
        <View style={styles.column}>{children}</View>
      </ScrollView>
    </SafeAreaView>
  );
}

export function Title({ children }: { children: ReactNode }) {
  return (
    <Text accessibilityRole="header" style={styles.title}>
      {children}
    </Text>
  );
}

export function Body({ children, muted }: { children: ReactNode; muted?: boolean }) {
  return <Text style={[styles.body, muted && styles.muted]}>{children}</Text>;
}

export function ErrorText({ children }: { children?: ReactNode }) {
  return children ? (
    <Text accessibilityRole="alert" style={styles.error}>
      {children}
    </Text>
  ) : null;
}

export function Button({
  label,
  accessibilityLabel = label,
  onPress,
  busy,
  disabled,
  kind = 'primary',
}: {
  label: string;
  /** What screen readers say, when the visible label alone is unclear (a row of "Add" buttons). */
  accessibilityLabel?: string;
  onPress: () => void;
  busy?: boolean;
  disabled?: boolean;
  kind?: 'primary' | 'secondary' | 'danger';
}) {
  const inactive = disabled || busy;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      accessibilityState={{ disabled: inactive, busy }}
      disabled={inactive}
      onPress={onPress}
      style={({ pressed }) => [styles.button, styles[kind], (pressed || inactive) && styles.dimmed]}
    >
      {busy ? (
        <ActivityIndicator color={kind === 'secondary' ? colors.text : colors.onAccent} />
      ) : (
        <Text style={[styles.buttonLabel, kind === 'secondary' && styles.secondaryLabel]}>{label}</Text>
      )}
    </Pressable>
  );
}

export function Field({ label, error, ...input }: TextInputProps & { label: string; error?: string }) {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput
        accessibilityLabel={label}
        autoCapitalize="none"
        autoCorrect={false}
        placeholderTextColor={colors.muted}
        style={[styles.input, error ? styles.inputError : null]}
        {...input}
      />
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

export function Loading() {
  return (
    <View style={[styles.safe, styles.center]}>
      <ActivityIndicator accessibilityLabel="Loading" color={colors.accent} size="large" />
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  center: { alignItems: 'center', justifyContent: 'center' },
  scroll: { flexGrow: 1, padding: space.lg },
  column: { width: '100%', maxWidth: 480, alignSelf: 'center', gap: space.md },
  title: { color: colors.text, fontSize: 28, fontWeight: '700', marginBottom: space.sm },
  body: { color: colors.text, fontSize: 16, lineHeight: 22 },
  muted: { color: colors.muted },
  error: { color: colors.danger, fontSize: 14 },
  button: { minHeight: 48, borderRadius: 12, alignItems: 'center', justifyContent: 'center', paddingHorizontal: space.md },
  primary: { backgroundColor: colors.accent },
  secondary: { backgroundColor: 'transparent', borderWidth: 1, borderColor: colors.border },
  danger: { backgroundColor: colors.danger },
  dimmed: { opacity: 0.6 },
  buttonLabel: { color: colors.onAccent, fontSize: 16, fontWeight: '600' },
  secondaryLabel: { color: colors.text },
  field: { gap: space.xs },
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  input: {
    minHeight: 48,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: space.md,
    color: colors.text,
    backgroundColor: colors.surface,
    fontSize: 16,
  },
  inputError: { borderColor: colors.danger },
});
