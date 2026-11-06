import type { Api } from '../../api/client';
import type { ConnectResult } from './musicConnect';

// iOS/Android: there is no MusicKit module in Expo SDK 57, so the native app can't ask for a Music User Token yet.
// Follow-up: a native MusicKit module (docs/adr/0007-music-providers.md, "Not verified"). Until then the fan
// connects Apple Music from the web app; the connection then counts everywhere, since it lives on the server.

export async function connectAppleMusic(_api: Api): Promise<ConnectResult | 'cancelled'> {
  return { ok: false, message: 'Apple Music connects from the web app for now.' };
}
