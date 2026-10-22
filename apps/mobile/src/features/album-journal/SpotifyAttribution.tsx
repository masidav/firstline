import { Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { colors, space } from '../../ui/theme';

/**
 * Required next to any Spotify metadata or artwork (ADR 0006): say it comes from Spotify and link back.
 * TODO(design): replace the wordmark text with Spotify's official logo asset per their branding guidelines.
 */
export function SpotifyAttribution({ url }: { url?: string }) {
  return (
    <View style={styles.row}>
      <Text style={styles.text}>Album details and artwork from </Text>
      {url ? (
        <Pressable accessibilityRole="link" accessibilityLabel="Open on Spotify" onPress={() => void Linking.openURL(url)}>
          <Text style={styles.link}>Spotify</Text>
        </Pressable>
      ) : (
        <Text style={styles.link}>Spotify</Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', marginTop: space.sm },
  text: { color: colors.muted, fontSize: 13 },
  link: { color: colors.text, fontSize: 13, fontWeight: '700' },
});
