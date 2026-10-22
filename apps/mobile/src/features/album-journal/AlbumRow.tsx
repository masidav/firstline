import { router } from 'expo-router';
import { Image, Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, space } from '../../ui/theme';

/** One album in a list: cover (unaltered, as Spotify requires), title, and a note. Opens the album. */
export function AlbumRow({
  albumId,
  title,
  coverUrl,
  note,
  trailing,
}: {
  albumId: string;
  title: string;
  coverUrl?: string | null;
  note?: string;
  trailing?: string;
}) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={`Open ${title}`} onPress={() => router.push(`/album/${albumId}`)} style={styles.row}>
      {coverUrl ? (
        <Image source={{ uri: coverUrl }} style={styles.cover} accessibilityIgnoresInvertColors />
      ) : (
        <View style={[styles.cover, styles.placeholder]}>
          <Text style={styles.placeholderText}>♪</Text>
        </View>
      )}
      <View style={styles.text}>
        <Text style={styles.title} numberOfLines={2}>
          {title}
        </Text>
        {note ? <Text style={styles.note}>{note}</Text> : null}
      </View>
      {trailing ? <Text style={styles.trailing}>{trailing}</Text> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  cover: { width: 56, height: 56, borderRadius: 4, backgroundColor: colors.surface },
  placeholder: { alignItems: 'center', justifyContent: 'center' },
  placeholderText: { color: colors.muted, fontSize: 20 },
  text: { flex: 1, gap: 2 },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
  trailing: { color: colors.text, fontSize: 14 },
});
