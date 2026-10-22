import type { ReactNode } from 'react';
import { Image, Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, space } from '../../ui/theme';

/** One artist in a list: photo, name, a short note under it, and an optional action on the right. */
export function ArtistRow({
  name,
  imageUrl,
  note,
  children,
  onPress,
}: {
  name: string;
  imageUrl?: string | null;
  note?: string;
  children?: ReactNode;
  /** When set, the row itself is a button (e.g. opens the artist's albums). */
  onPress?: () => void;
}) {
  const Row = onPress ? Pressable : View;
  return (
    <Row
      style={styles.row}
      {...(onPress ? { onPress, accessibilityRole: 'button' as const, accessibilityLabel: `Open ${name}` } : {})}
    >
      {imageUrl ? (
        <Image source={{ uri: imageUrl }} style={styles.image} accessibilityIgnoresInvertColors />
      ) : (
        <View style={styles.image} />
      )}
      <View style={styles.rowText}>
        <Text style={styles.name} numberOfLines={1}>
          {name}
        </Text>
        {note ? <Text style={styles.note}>{note}</Text> : null}
      </View>
      {children ? <View style={styles.rowAction}>{children}</View> : null}
    </Row>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  image: { width: 48, height: 48, borderRadius: 24, backgroundColor: colors.surface },
  rowText: { flex: 1, gap: 2 },
  rowAction: { minWidth: 88 },
  name: { color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
});
