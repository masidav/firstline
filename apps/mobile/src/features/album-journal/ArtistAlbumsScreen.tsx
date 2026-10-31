import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { AlbumRow } from './AlbumRow';
import { type Album, describeAlbum } from './journal';
import { SpotifyAttribution } from './SpotifyAttribution';
import { SpotifyEmbed } from '../spotify/SpotifyEmbed';

/** An artist's releases from the catalog (GET /artists/{id}/albums), newest first. */
export function ArtistAlbumsScreen({ artistId }: { artistId: string }) {
  const { session } = useSession();
  const [albums, setAlbums] = useState<Album[] | null>(null);
  const [spotifyArtistId, setSpotifyArtistId] = useState<string>();
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const page = unwrap(
        await session.api.GET('/artists/{artistId}/albums', { params: { path: { artistId }, query: { limit: 100 } } }),
      );
      setAlbums(page.items);
    } catch (e) {
      setError(errorMessage(e));
    }
    // The artist's Spotify id, for the embedded player on the web build; the page is fine without it.
    try {
      setSpotifyArtistId(unwrap(await session.api.GET('/artists/{artistId}', { params: { path: { artistId } } })).spotify_artist_id);
    } catch {
      setSpotifyArtistId(undefined);
    }
  }, [session, artistId]);

  useEffect(() => {
    void load();
  }, [load]);

  const artistName = albums?.[0]?.artist.name;
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>{artistName ?? 'Albums'}</Title>
      <SpotifyEmbed kind="artist" id={spotifyArtistId} title={artistName ?? 'Artist'} />
      <Button label="Upcoming shows" kind="secondary" onPress={() => router.push(`/artist/${artistId}/shows`)} />
      {error && (
        <>
          <ErrorText>{error}</ErrorText>
          <Button label="Try again" kind="secondary" onPress={() => void load()} />
        </>
      )}
      {!albums && !error && <Loading />}
      {albums?.length === 0 && <Body muted>No albums here yet. New artists get their albums within a few minutes.</Body>}
      {albums?.map((album) => (
        <AlbumRow
          key={album.id}
          albumId={album.id}
          title={album.title}
          coverUrl={album.cover_url}
          note={describeAlbum(album)}
          trailing={album.community?.log_count ? `${album.community.log_count} logged` : undefined}
        />
      ))}
      {albums && albums.length > 0 && <SpotifyAttribution />}
    </Screen>
  );
}
