import { Platform } from 'react-native';

import { authorizeAppleMusic, isAppleMusicKitAvailable } from '../../../modules/apple-music-kit';
import type { Api } from '../../api/client';
import { connectAppleMusicWith, type ConnectResult } from './musicConnect';

// iOS: Apple's MusicKit through the local Expo module (modules/apple-music-kit). The system asks the fan to allow
// Apple Music access, MusicKit issues a Music User Token for the API's developer token, and the token goes to
// PUT /me/music/apple_music (connectAppleMusicWith, shared with the web). Android: Apple has no MusicKit for
// third-party Android apps, so the fan connects from the web app; the connection lives on the server and counts
// everywhere. Not run on a device yet (the module needs an iOS build; README).

/** Shown on Android, and in the Music screen instead of the Connect button there. */
export const APPLE_MUSIC_WEB_ONLY = 'Apple Music connects from the web app for now. A connection made there counts here too.';

export async function connectAppleMusic(api: Api): Promise<ConnectResult | 'cancelled'> {
  if (Platform.OS !== 'ios') {
    return { ok: false, message: APPLE_MUSIC_WEB_ONLY };
  }
  if (!isAppleMusicKitAvailable()) {
    // An iOS build without native modules (Expo Go) has no MusicKit.
    return { ok: false, message: "This build can't connect Apple Music. Connect from the web app." };
  }
  return connectAppleMusicWith(api, async (developerToken) => {
    try {
      return await authorizeAppleMusic(developerToken); // null when the fan didn't allow access
    } catch {
      // MusicKit refused to issue a token: most often an Apple ID without an Apple Music subscription.
      throw new Error("Apple Music couldn't sign you in. Check that this Apple ID has an Apple Music subscription, then try again.");
    }
  });
}
