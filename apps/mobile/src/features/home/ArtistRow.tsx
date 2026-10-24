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
  // The action on the right stays outside the pressable area, so buttons are never nested.
  const body = (
    <>
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
    </>
  );
  return (
    <View style={styles.row}>
      {onPress ? (
        <Pressable style={styles.main} onPress={onPress} accessibilityRole="button" accessibilityLabel={`Open ${name}`}>
          {body}
        </Pressable>
      ) : (
        <View style={styles.main}>{body}</View>
      )}
      {children ? <View style={styles.rowAction}>{children}</View> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  main: { flex: 1, flexDirection: 'row', alignItems: 'center', gap: space.md },
  image: { width: 48, height: 48, borderRadius: 24, backgroundColor: colors.surface },
  rowText: { flex: 1, gap: 2 },
  rowAction: { minWidth: 88 },
  name: { color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
});
