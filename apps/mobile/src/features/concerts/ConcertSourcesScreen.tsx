import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { type Artist, type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';

type ArtistSource = components['schemas']['ArtistSource'];

const EXPLORER = 'https://developer.ticketmaster.com/api-explorer/v2/';

/** "Linked by Spotify page", "Linked by staff", or "No match found". */
export function linkLabel(source: ArtistSource | undefined): string {
  if (!source) return 'Not looked up yet';
  if (!source.external_id) return 'No match found (looked up again weekly)';
  return source.matched_by === 'staff' ? 'Linked by staff' : 'Linked by its Spotify page';
}

/**
 * Staff (route /concert-sources): which Ticketmaster artist each tracked artist is. The worker links an artist by itself
 * only when Ticketmaster lists the artist's Spotify page; staff link the rest with the Ticketmaster attraction id, or
 * remove a wrong link (GET/PUT/DELETE /admin/artists/{id}/sources/ticketmaster).
 */
export function ConcertSourcesScreen() {
  const { session } = useSession();
  const [query, setQuery] = useState('');
  const [artists, setArtists] = useState<Artist[]>([]);
  const [artist, setArtist] = useState<Artist | null>(null);
  const [source, setSource] = useState<ArtistSource | undefined>();
  const [attractionId, setAttractionId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();

  const search = useCallback(async () => {
    setError(undefined);
    try {
      const page = unwrap(
        await session.api.GET('/artists', { params: { query: { q: query.trim() || undefined, status: 'all', sort: 'name', limit: 20 } } }),
      );
      setArtists(page.items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, query]);

  // Lists artists on open; after that, searching is on demand (not on every keystroke).
  const [searchOnOpen] = useState(() => search);
  useEffect(() => {
    void searchOnOpen();
  }, [searchOnOpen]);

  async function open(next: Artist) {
    setArtist(next);
    setMessage(undefined);
    setError(undefined);
    setAttractionId('');
    try {
      const list = unwrap(await session.api.GET('/admin/artists/{artistId}/sources', { params: { path: { artistId: next.id } } }));
      setSource(list.items.find((s) => s.source === 'ticketmaster'));
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  async function link() {
    if (!artist) return;
    setBusy(true);
    setError(undefined);
    try {
      const linked = unwrap(
        await session.api.PUT('/admin/artists/{artistId}/sources/{source}', {
          params: { path: { artistId: artist.id, source: 'ticketmaster' } },
          body: { external_id: attractionId.trim() },
        }),
      );
      setSource(linked);
      setAttractionId('');
      setMessage('Linked. Its shows load within a few minutes.');
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function unlink() {
    if (!artist) return;
    setBusy(true);
    setError(undefined);
    try {
      unwrap(
        await session.api.DELETE('/admin/artists/{artistId}/sources/{source}', {
          params: { path: { artistId: artist.id, source: 'ticketmaster' } },
        }),
      );
      setSource(undefined);
      setMessage('Link removed. Shows already loaded stay.');
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/profile'))} />
      <Title>Concert sources</Title>
      <Body muted>
        Shows come from Ticketmaster. Artists are linked by themselves when Ticketmaster lists their Spotify page; link
        the rest here with the Ticketmaster attraction id.
      </Body>
      <Field label="Find a tracked artist" value={query} onChangeText={setQuery} onSubmitEditing={() => void search()} />
      <Button label="Search" kind="secondary" onPress={() => void search()} />
      <ErrorText>{error}</ErrorText>

      {artist ? (
        <View style={styles.card}>
          <Text style={styles.name}>{artist.name}</Text>
          <Text style={styles.muted}>Ticketmaster: {linkLabel(source)}</Text>
          {source?.external_id ? <Text style={styles.muted}>Attraction id: {source.external_id}</Text> : null}
          {source?.synced_at ? (
            <Text style={styles.muted}>Shows last loaded {new Date(source.synced_at).toLocaleString()}</Text>
          ) : null}
          <Field
            label="Ticketmaster attraction id"
            value={attractionId}
            onChangeText={setAttractionId}
            autoCapitalize="none"
            placeholder="K8vZ9171ob7"
          />
          <Pressable accessibilityRole="link" onPress={() => void Linking.openURL(EXPLORER)}>
            <Text style={styles.link}>Find it in Ticketmaster's API explorer (attraction search)</Text>
          </Pressable>
          <Button label="Link to Ticketmaster" busy={busy} disabled={!attractionId.trim()} onPress={() => void link()} />
          {source?.external_id ? <Button label="Remove link" kind="danger" busy={busy} onPress={() => void unlink()} /> : null}
          {message && <Body>{message}</Body>}
          <Button label="Pick another artist" kind="secondary" onPress={() => setArtist(null)} />
        </View>
      ) : (
        artists.map((a) => (
          <Pressable key={a.id} accessibilityRole="button" accessibilityLabel={`Open ${a.name}`} onPress={() => void open(a)} style={styles.row}>
            <Text style={styles.name}>{a.name}</Text>
            <Text style={styles.muted}>{a.status}</Text>
          </Pressable>
        ))
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  row: { flexDirection: 'row', justifyContent: 'space-between', paddingVertical: space.sm },
  name: { color: colors.text, fontSize: 16, fontWeight: '600' },
  muted: { color: colors.muted, fontSize: 14 },
  link: { color: colors.accent, fontSize: 14, fontWeight: '600' },
});
