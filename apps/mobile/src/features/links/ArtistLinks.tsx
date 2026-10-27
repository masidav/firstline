import { StyleSheet, View } from 'react-native';

import type { Artist } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { Button } from '../../ui/components';
import { space } from '../../ui/theme';
import { openTracked, spotifyArtistUrl } from './openLink';

/** The artist's website, merch store (when staff or Ticketmaster set them), and Spotify page. */
export function ArtistLinks({ artist }: { artist: Artist }) {
  const { session } = useSession();
  const ids = { artistId: artist.id };
  return (
    <View style={styles.row}>
      {artist.merch_url ? (
        <Button label="Merch" kind="secondary" onPress={() => openTracked(session.api, 'artist_merch', artist.merch_url!, ids)} />
      ) : null}
      {artist.website_url ? (
        <Button label="Website" kind="secondary" onPress={() => openTracked(session.api, 'artist_website', artist.website_url!, ids)} />
      ) : null}
      <Button
        label="Spotify"
        kind="secondary"
        onPress={() => openTracked(session.api, 'artist_spotify', spotifyArtistUrl(artist.spotify_artist_id), ids)}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
});
