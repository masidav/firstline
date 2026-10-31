import { createElement } from 'react';
import { Platform, StyleSheet, View } from 'react-native';

import { space } from '../../ui/theme';

/** A Spotify id as Spotify issues them (22 base62 characters); the dev seed's made-up ids are not. */
const SPOTIFY_ID = /^[0-9A-Za-z]{22}$/;

export type SpotifyEmbedKind = 'album' | 'artist' | 'track';

/**
 * Spotify's embedded player (https://developer.spotify.com/documentation/embeds) for an album, artist, or track, on
 * the web build only: an iframe that plays 30-second previews for anyone and full songs for a visitor signed in to
 * Spotify in the browser. Phones keep the Play buttons (Spotify Connect) and the links into the Spotify app. Nothing
 * renders for ids that aren't real Spotify ids (seed data) so the player never shows an error card.
 */
export function SpotifyEmbed({ kind, id, title }: { kind: SpotifyEmbedKind; id: string | null | undefined; title: string }) {
  if (Platform.OS !== 'web' || !id || !SPOTIFY_ID.test(id)) return null;
  const height = kind === 'track' ? 152 : 352;
  return (
    <View style={styles.wrap} testID={`spotify-embed-${kind}`}>
      {createElement('iframe', {
        src: `https://open.spotify.com/embed/${kind}/${id}?utm_source=generator`,
        title: `${title} on Spotify`,
        loading: 'lazy',
        allow: 'autoplay; clipboard-write; encrypted-media; fullscreen; picture-in-picture',
        style: { width: '100%', height, border: 0, borderRadius: 12 },
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { marginTop: space.md },
});
