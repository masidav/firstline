import { router } from 'expo-router';
import { useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { fanName } from '../follows/follows';
import type { BouncerDoor } from './BouncerDoorScreen';

/**
 * The bouncer's door inside the feed (decided 2026-10-20): after some scrolling, a door; knocking draws a real song
 * from the grab bag of someone the fan follows (GET /me/bouncer-door), with whose it is and a Spotify link.
 */
export function BouncerDoorCard() {
  const { session } = useSession();
  const [draw, setDraw] = useState<BouncerDoor | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function knock() {
    setBusy(true);
    setError(undefined);
    try {
      setDraw(unwrap(await session.api.GET('/me/bouncer-door')));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const song = draw?.song;
  return (
    <View style={styles.card}>
      <View style={styles.doorRow}>
        <View style={[styles.door, song && styles.doorOpen]} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
          <View style={styles.knob} />
        </View>
        <View style={styles.doorText}>
          <Text style={styles.kicker}>The bouncer's door</Text>
          <Text style={styles.title}>{song ? `${fanName(draw?.owner)} let you in` : 'Knock to hear what someone you follow loves'}</Text>
        </View>
      </View>

      {song && (
        <>
          <View style={styles.songRow}>
            {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
            <View style={styles.songText}>
              <Text style={styles.song} numberOfLines={2}>{song.title}</Text>
              <Text style={styles.muted} numberOfLines={1}>
                {song.artist.name}
                {song.album_title ? ` · ${song.album_title}` : ''}
              </Text>
            </View>
          </View>
          <Pressable accessibilityRole="link" accessibilityLabel={`Open ${song.title} on Spotify`} onPress={() => void Linking.openURL(song.spotify_url)} hitSlop={8}>
            <Text style={styles.link}>Open in Spotify</Text>
          </Pressable>
        </>
      )}
      {draw && !song && draw.following === 0 && (
        <>
          <Body muted>Nobody answers: you don't follow anyone yet.</Body>
          <Button label="Find fans to follow" kind="secondary" onPress={() => router.push('/people')} />
        </>
      )}
      {draw && !song && draw.following > 0 && <Body muted>Nobody you follow has a song in their grab bag yet.</Body>}
      <ErrorText>{error}</ErrorText>
      <Button label={draw ? 'Knock again' : 'Knock'} accessibilityLabel={draw ? 'Knock again on the bouncer door' : 'Knock on the bouncer door'}
        busy={busy} onPress={() => void knock()} />
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 2, borderColor: '#b3261e', backgroundColor: colors.surface },
  doorRow: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  door: { width: 40, height: 60, borderRadius: 4, backgroundColor: '#5b3a1e', borderWidth: 2, borderColor: '#8a5a2b', justifyContent: 'center' },
  doorOpen: { backgroundColor: '#1d140b', borderColor: colors.accent },
  knob: { width: 6, height: 6, borderRadius: 3, backgroundColor: '#e2b04a', marginLeft: 26 },
  doorText: { flex: 1, gap: 2 },
  kicker: { color: '#e57373', fontSize: 13, fontWeight: '700', textTransform: 'uppercase', letterSpacing: 0.5 },
  title: { color: colors.text, fontSize: 16, fontWeight: '700' },
  songRow: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  cover: { width: 56, height: 56, borderRadius: 6, backgroundColor: colors.background },
  songText: { flex: 1, gap: 2 },
  song: { color: colors.text, fontSize: 16, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
  link: { color: colors.accent, fontSize: 15, fontWeight: '600' },
});
