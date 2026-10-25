import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';

export type GrabBag = components['schemas']['GrabBag'];
export type GrabBagSong = components['schemas']['GrabBagSong'];
export type TrackSearchResult = components['schemas']['TrackSearchResult'];

/** The first empty slot (1-based), or null when every slot is taken. */
export function firstEmptySlot(bag: Pick<GrabBag, 'items' | 'slots'>): number | null {
  for (let slot = 1; slot <= bag.slots; slot++) {
    if (!bag.items.some((song) => song.position === slot)) return slot;
  }
  return null;
}

/**
 * Your grab bag (GET /me/grab-bag): five favourite songs in numbered slots, shared with your followers, each
 * linking to Spotify. Search Spotify for a song (GET /tracks/search) and add it to the first empty slot
 * (PUT /me/grab-bag/{position}); adding a song by an artist who isn't here yet adds that artist too.
 */
export function GrabBagScreen() {
  const { session } = useSession();
  const [bag, setBag] = useState<GrabBag | null>(null);
  const [error, setError] = useState<string>();
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<TrackSearchResult[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setBag(unwrap(await session.api.GET('/me/grab-bag')));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function search() {
    const text = query.trim();
    setSearchError(undefined);
    setNotice(undefined);
    if (text.length < 2) {
      setSearchError('Type at least 2 letters to search Spotify.');
      return;
    }
    setSearching(true);
    try {
      setResults(unwrap(await session.api.GET('/tracks/search', { params: { query: { q: text } } })).items);
    } catch (e) {
      setSearchError(errorMessage(e));
    } finally {
      setSearching(false);
    }
  }

  async function add(track: TrackSearchResult) {
    if (!bag) return;
    const slot = firstEmptySlot(bag);
    if (slot === null) {
      setNotice('Your grab bag is full. Remove a song to make room.');
      return;
    }
    setBusy(track.spotify_track_id);
    setNotice(undefined);
    try {
      setBag(unwrap(await session.api.PUT('/me/grab-bag/{position}', { params: { path: { position: slot } }, body: { spotify_track_id: track.spotify_track_id } })));
      const primary = track.artists[0];
      setNotice(
        primary && !primary.artist_id
          ? `Added "${track.title}". ${primary.name} is new here; we're loading their albums.`
          : `Added "${track.title}".`,
      );
    } catch (e) {
      setNotice(undefined);
      setSearchError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function remove(song: GrabBagSong) {
    setBusy(song.id);
    setNotice(undefined);
    try {
      unwrap(await session.api.DELETE('/me/grab-bag/{position}', { params: { path: { position: song.position } } }));
      setBag((current) => current && { ...current, items: current.items.filter((s) => s.id !== song.id) });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const inBag = new Set(bag?.items.map((song) => song.spotify_track_id));
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Your grab bag</Title>
      <Body muted>
        Five songs you love. Fans who follow you see them on their feed with your name, and each one opens in Spotify.
      </Body>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {!bag && !error && <Loading />}
      {bag &&
        Array.from({ length: bag.slots }, (_, index) => index + 1).map((slot) => {
          const song = bag.items.find((s) => s.position === slot);
          return song ? (
            <View key={slot} style={styles.slot}>
              {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
              <View style={styles.slotText}>
                <Text style={styles.muted}>Song {slot}</Text>
                <Text style={styles.strong} numberOfLines={2}>
                  {song.title}
                </Text>
                <Text style={styles.muted} numberOfLines={1}>
                  {song.artist.name}
                  {song.album_title ? ` · ${song.album_title}` : ''}
                  {song.artist_status === 'onboarding' ? ' · artist getting ready' : ''}
                </Text>
                <Pressable
                  accessibilityRole="link"
                  accessibilityLabel={`Open ${song.title} on Spotify`}
                  onPress={() => void Linking.openURL(song.spotify_url)}
                  hitSlop={8}
                >
                  <Text style={styles.link}>Open in Spotify</Text>
                </Pressable>
              </View>
              <Button label="Remove" accessibilityLabel={`Remove ${song.title}`} kind="secondary" busy={busy === song.id} onPress={() => void remove(song)} />
            </View>
          ) : (
            <View key={slot} style={[styles.slot, styles.empty]}>
              <Text style={styles.muted}>Song {slot} · empty</Text>
            </View>
          );
        })}

      <Field
        label="Search songs on Spotify"
        placeholder="Song or artist"
        value={query}
        onChangeText={(text) => {
          setQuery(text);
          setResults(null);
          setSearchError(undefined);
        }}
        onSubmitEditing={() => void search()}
        returnKeyType="search"
        autoCapitalize="words"
        maxLength={100}
      />
      {searching && <Body muted>Looking on Spotify…</Body>}
      <ErrorText>{searchError}</ErrorText>
      {notice && <Body>{notice}</Body>}
      {results?.length === 0 && <Body muted>Spotify has no song matching "{query.trim()}".</Body>}
      {results?.map((track) => (
        <View key={track.spotify_track_id} style={styles.slot}>
          {track.cover_url ? <Image source={{ uri: track.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
          <View style={styles.slotText}>
            <Text style={styles.strong} numberOfLines={2}>
              {track.title}
            </Text>
            <Text style={styles.muted} numberOfLines={1}>
              {track.artists.map((artist) => artist.name).join(', ')}
              {track.album_title ? ` · ${track.album_title}` : ''}
            </Text>
          </View>
          {inBag.has(track.spotify_track_id) ? (
            <Text style={styles.muted}>In your bag</Text>
          ) : (
            <Button
              label="Add"
              accessibilityLabel={`Add ${track.title}`}
              busy={busy === track.spotify_track_id}
              disabled={busy !== null}
              onPress={() => void add(track)}
            />
          )}
        </View>
      ))}
    </Screen>
  );
}

const styles = StyleSheet.create({
  slot: { flexDirection: 'row', alignItems: 'center', gap: space.md, padding: space.sm, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  empty: { borderStyle: 'dashed', minHeight: 56 },
  cover: { width: 56, height: 56, borderRadius: 6, backgroundColor: colors.surface },
  slotText: { flex: 1, gap: 2 },
  strong: { color: colors.text, fontSize: 16, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
  link: { color: colors.accent, fontSize: 14, fontWeight: '600' },
});
