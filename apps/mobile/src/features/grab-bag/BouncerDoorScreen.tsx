import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { fanName } from '../follows/follows';
import { PlayButton } from '../spotify/NowPlaying';

export type BouncerDoor = components['schemas']['BouncerDoor'];

/**
 * The bouncer door (GET /me/bouncer-door): one song drawn from the grab bags of the fans you follow, with whose it
 * is, and a link to play it on Spotify. "Draw another" asks again. Decided 2026-10-20: a recommended real track,
 * based on the taste of someone the fan follows.
 */
export function BouncerDoorScreen() {
  const { session } = useSession();
  const [draw, setDraw] = useState<BouncerDoor | null>(null);
  const [drawing, setDrawing] = useState(false);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setDrawing(true);
    setError(undefined);
    try {
      setDraw(unwrap(await session.api.GET('/me/bouncer-door')));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setDrawing(false);
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  const song = draw?.song;
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Bouncer door</Title>
      <Body muted>A song picked from the grab bags of the fans you follow. Something they love; maybe you will too.</Body>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {!draw && !error && <Loading />}

      {song && draw && (
        <View style={styles.card}>
          <Text style={styles.kicker}>From {fanName(draw.owner)}'s grab bag</Text>
          <View style={styles.row}>
            {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
            <View style={styles.rowText}>
              <Text style={styles.title}>{song.title}</Text>
              <Text style={styles.muted}>
                {song.artist.name}
                {song.album_title ? ` · ${song.album_title}` : ''}
              </Text>
            </View>
          </View>
          <View style={styles.songActions}>
            <PlayButton spotifyTrackId={song.spotify_track_id} spotifyUrl={song.spotify_url} title={song.title} />
            <Pressable
              accessibilityRole="link"
              accessibilityLabel={`Open ${song.title} on Spotify`}
              onPress={() => void Linking.openURL(song.spotify_url)}
              hitSlop={8}
            >
              <Text style={styles.link}>Open in Spotify</Text>
            </Pressable>
          </View>
        </View>
      )}
      {draw && !song && draw.following === 0 && (
        <>
          <Body>The door is shut: you don't follow anyone yet.</Body>
          <Button label="Find fans to follow" onPress={() => router.push('/people')} />
        </>
      )}
      {draw && !song && draw.following > 0 && (
        <Body>Nobody you follow has put a song in their grab bag yet. Fill yours and they might follow suit.</Body>
      )}
      {draw && song && <Button label="Draw another" busy={drawing} onPress={() => void load()} />}
      <Button label="Your grab bag" kind="secondary" onPress={() => router.push('/grab-bag')} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.accent, backgroundColor: colors.surface },
  kicker: { color: colors.muted, fontSize: 13, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },
  row: { flexDirection: 'row', gap: space.md, alignItems: 'center' },
  cover: { width: 72, height: 72, borderRadius: 8, backgroundColor: colors.background },
  rowText: { flex: 1, gap: 2 },
  title: { color: colors.text, fontSize: 20, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
  link: { color: colors.accent, fontSize: 16, fontWeight: '600' },
  songActions: { flexDirection: 'row', alignItems: 'center', gap: space.md },
});
