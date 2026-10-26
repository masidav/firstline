import { router } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type Artist, type ArtistSearchResult, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { ArtistRow } from './ArtistRow';
import { usePendingLogs } from '../album-journal/journalQueue';
import { CameraIcon } from '../../ui/CameraIcon';
import { Feed } from '../feed/Feed';
import { FeaturedArtistCard } from '../featured/FeaturedArtistCard';
import { AlertsButton } from '../notifications/AlertsButton';
import { DailyPromptCard } from '../prompts/DailyPromptCard';
import { DailyTriviaCard } from '../trivia/DailyTriviaCard';
import { YourArtists } from './YourArtists';
import { useSyncOnOpen } from '../spotify/backgroundSync';
import type { SpotifyStatus } from '../spotify/spotifyConnect';

const DEBOUNCE_MS = 300;

/**
 * Home after sign-in: one vertical scroll (Feed). While the search box is empty its header shows the fan's artists
 * with their scores (YourArtists) and a button to post a photo, and below them the mixed feed. Otherwise a
 * search of the artists already on Fandom Score as you type (GET /artists, no Spotify calls), with the feed hidden.
 * Pressing Enter with no exact match asks Spotify (GET /artists/search) and offers to add an artist (POST /artists).
 */
export function HomeScreen() {
  const { session, state } = useSession();
  // A quiet Spotify sync each time the app opens (the server skips it when the last one is recent).
  const updateSpotify = useCallback((spotify: SpotifyStatus) => session.updateMe({ spotify }), [session]);
  useSyncOnOpen(session.api, state.status === 'signedIn' && state.me.spotify?.connected === true, updateSpotify);
  const pendingLogs = usePendingLogs();
  const [query, setQuery] = useState('');
  const [tracked, setTracked] = useState<Artist[] | null>(null);
  const [searchError, setSearchError] = useState<string>();
  const [spotify, setSpotify] = useState<{ query: string; items: ArtistSearchResult[] } | null>(null);
  const [askingSpotify, setAskingSpotify] = useState(false);
  const [adding, setAdding] = useState<string | null>(null);
  const [added, setAdded] = useState<string>();
  const [spotifyError, setSpotifyError] = useState<string>();
  // Only the newest search may show its results; slower, older responses are dropped.
  const latest = useRef(0);

  const searchTracked = useCallback(
    async (text: string): Promise<Artist[] | null> => {
      const request = ++latest.current;
      try {
        const page = unwrap(
          await session.api.GET('/artists', { params: { query: { q: text, status: 'all', sort: 'name', limit: 20 } } }),
        );
        if (request !== latest.current) return null;
        setTracked(page.items);
        setSearchError(undefined);
        return page.items;
      } catch (e) {
        if (request === latest.current) setSearchError(errorMessage(e));
        return null;
      }
    },
    [session],
  );

  useEffect(() => {
    const text = query.trim();
    if (!text) {
      latest.current++;
      setTracked(null);
      setSearchError(undefined);
      return;
    }
    const timer = setTimeout(() => void searchTracked(text), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [query, searchTracked]);

  function edit(text: string) {
    setQuery(text);
    setSpotify(null);
    setSpotifyError(undefined);
    setAdded(undefined);
  }

  async function submit() {
    const text = query.trim();
    if (!text) return;
    setSpotifyError(undefined);
    setAdded(undefined);
    const items = await searchTracked(text);
    if (!items || items.some((artist) => artist.name.toLowerCase() === text.toLowerCase())) {
      setSpotify(null);
      return;
    }
    if (text.length < 2) {
      setSpotifyError('Type at least 2 letters to search Spotify.');
      return;
    }
    setAskingSpotify(true);
    try {
      const results = unwrap(await session.api.GET('/artists/search', { params: { query: { q: text } } }));
      setSpotify({ query: text, items: results.items });
    } catch (e) {
      setSpotifyError(errorMessage(e));
    } finally {
      setAskingSpotify(false);
    }
  }

  async function add(result: ArtistSearchResult) {
    setAdding(result.spotify_artist_id);
    setSpotifyError(undefined);
    try {
      const artist = unwrap(await session.api.POST('/artists', { body: { spotify_artist_id: result.spotify_artist_id } }));
      setSpotify((current) =>
        current && {
          ...current,
          items: current.items.map((item) =>
            item.spotify_artist_id === artist.spotify_artist_id
              ? { ...item, tracked: true, artist_id: artist.id, status: artist.status }
              : item,
          ),
        },
      );
      setAdded(`Added ${artist.name}. We're loading their albums, which can take a few minutes.`);
      void searchTracked(query.trim());
    } catch (e) {
      setSpotifyError(errorMessage(e));
    } finally {
      setAdding(null);
    }
  }

  const text = query.trim();
  // The home page is the feed's scroll; everything above the feed is its header.
  const header = (
    <>
      <View style={styles.header}>
        <Title>Your artists</Title>
      </View>
      <View style={styles.headerButtons}>
        <AlertsButton />
        <Button label="Tickets" kind="secondary" onPress={() => router.push('/tickets')} />
        <Button label="Journal" kind="secondary" onPress={() => router.push('/journal')} />
        <Button label="People" kind="secondary" onPress={() => router.push('/people')} />
        <Button label="Profile" kind="secondary" onPress={() => router.push('/profile')} />
      </View>
      {pendingLogs.length > 0 && (
        <Pressable accessibilityRole="button" onPress={() => router.push('/journal')} style={styles.pendingBanner}>
          <Text style={styles.pendingText}>
            {pendingLogs.length === 1 ? '1 journal entry' : `${pendingLogs.length} journal entries`} waiting to sync. Tap to see.
          </Text>
        </Pressable>
      )}

      <Field
        label="Search artists"
        placeholder="Artist name"
        value={query}
        onChangeText={edit}
        onSubmitEditing={() => void submit()}
        returnKeyType="search"
        autoCapitalize="words"
        maxLength={100}
      />
      <ErrorText>{searchError}</ErrorText>

      {!text && (
        <>
          <FeaturedArtistCard />
          <YourArtists />
          <DailyTriviaCard />
          <DailyPromptCard />
        </>
      )}
      {text !== '' && tracked?.length === 0 && !spotify && !askingSpotify && (
        <Body muted>No artist here matches "{text}" yet. Press Enter to look on Spotify.</Body>
      )}
      {tracked?.map((artist) => (
        <ArtistRow
          key={artist.id}
          name={artist.name}
          imageUrl={artist.image_url}
          note={artist.status === 'onboarding' ? 'Getting ready' : undefined}
          onPress={() => router.push(`/artist/${artist.id}/albums`)}
        />
      ))}

      {askingSpotify && <Body muted>Looking on Spotify…</Body>}
      {spotify && (
        <>
          <Text accessibilityRole="header" style={styles.section}>
            On Spotify
          </Text>
          {spotify.items.length === 0 && <Body muted>Spotify has no artist matching "{spotify.query}".</Body>}
          {spotify.items.map((result) => (
            <ArtistRow
              key={result.spotify_artist_id}
              name={result.name}
              imageUrl={result.image_url}
              note={describeResult(result)}
            >
              {result.tracked ? (
                <Text style={styles.note}>Already here</Text>
              ) : (
                <Button
                  label="Add"
                  accessibilityLabel={`Add ${result.name}`}
                  onPress={() => void add(result)}
                  busy={adding === result.spotify_artist_id}
                  disabled={adding !== null}
                />
              )}
            </ArtistRow>
          ))}
        </>
      )}
      {added && <Body>{added}</Body>}
      <ErrorText>{spotifyError}</ErrorText>
    </>
  );
  // Posting a photo is a camera button floating over the feed (requested 2026-10-25), not a full-width button.
  const camera = !text && (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel="Post a photo"
      onPress={() => router.push('/post')}
      style={({ pressed }) => [styles.camera, pressed && styles.cameraPressed]}
    >
      <CameraIcon />
    </Pressable>
  );
  return <Feed header={header} active={!text} overlay={camera} />;
}

/** Release count for same-named artists (so the real one stands out), otherwise genres if Spotify sent any. */
function describeResult(result: ArtistSearchResult): string | undefined {
  if (result.release_count != null) {
    return result.release_count === 1 ? '1 release' : `${result.release_count} releases`;
  }
  return result.genres?.slice(0, 2).join(', ') || undefined;
}

const styles = StyleSheet.create({
  header: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: space.md },
  // Wraps on narrow phones instead of pushing the last button off the screen.
  headerButtons: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  pendingBanner: { padding: space.sm, borderRadius: 8, borderWidth: 1, borderColor: colors.accent },
  pendingText: { color: colors.text, fontSize: 14 },
  section: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
  note: { color: colors.muted, fontSize: 14 },
  camera: {
    position: 'absolute', right: space.lg, bottom: space.lg, width: 60, height: 60, borderRadius: 30, backgroundColor: colors.accent,
    alignItems: 'center', justifyContent: 'center', shadowColor: '#000', shadowOpacity: 0.35, shadowRadius: 8, shadowOffset: { width: 0, height: 3 },
    elevation: 6,
  },
  cameraPressed: { opacity: 0.8 },
});
