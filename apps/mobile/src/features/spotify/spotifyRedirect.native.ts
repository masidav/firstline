import type { PendingConnect } from './spotifyConnect';

// iOS/Android: to do with the native build. Plan: expo-web-browser's openAuthSessionAsync with a
// `fandomscore://spotify-callback` redirect, then completeConnect() with the returned query parameters.

export function callbackUri(): string {
  return 'fandomscore://spotify-callback';
}

export async function startSpotifyConnect(_clientId: string): Promise<void> {
  throw new Error('Connecting Spotify in the native app is not built yet. Use the web app for now.');
}

export function takePendingConnect(): PendingConnect | null {
  return null;
}
