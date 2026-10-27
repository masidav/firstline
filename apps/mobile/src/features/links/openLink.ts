import { Linking } from 'react-native';

import type { Api } from '../../api/client';

export type LinkKind = 'artist_website' | 'artist_merch' | 'artist_spotify' | 'event_tickets';

/** The artist's Spotify page. */
export const spotifyArtistUrl = (spotifyArtistId: string) => `https://open.spotify.com/artist/${encodeURIComponent(spotifyArtistId)}`;

/**
 * Opens an outbound link and counts the tap (POST /clicks; anonymous, and a click, not a purchase). Counting never
 * delays or blocks opening the link.
 */
export function openTracked(api: Api, kind: LinkKind, url: string, ids: { artistId: string; eventId?: string }): void {
  void api
    .POST('/clicks', { body: { kind, artist_id: ids.artistId, ...(ids.eventId ? { event_id: ids.eventId } : {}) } })
    .catch(() => undefined);
  void Linking.openURL(url);
}
