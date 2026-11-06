import type { Api } from '../../api/client';
import { connectAppleMusicWith, type ConnectResult } from './musicConnect';

// Web: Apple Music through MusicKit JS (https://js-cdn.music.apple.com/musickit/v3/musickit.js), loaded the first
// time the fan presses Connect. The API signs the developer token with its MusicKit key
// (GET /me/music/apple_music/developer-token); MusicKit asks the fan to sign in to Apple Music and gives back a
// Music User Token, which goes to PUT /me/music/apple_music (the steps around MusicKit are shared with iOS:
// connectAppleMusicWith). The iPhone app uses the native MusicKit instead (appleMusicConnect.native.ts).

const MUSICKIT_SRC = 'https://js-cdn.music.apple.com/musickit/v3/musickit.js';

/** The little of MusicKit JS this app uses. */
interface MusicKitInstance {
  /** Resolves to the Music User Token, or nothing when the fan closed Apple's sign-in. */
  authorize(): Promise<string | undefined | null>;
}
interface MusicKitGlobal {
  configure(options: { developerToken: string; app: { name: string; build: string } }): Promise<unknown> | unknown;
  getInstance(): MusicKitInstance;
}
type MusicKitWindow = Window & { MusicKit?: MusicKitGlobal };

let loading: Promise<MusicKitGlobal> | undefined;

/** Loads MusicKit JS once, by injecting its script and waiting for the `musickitloaded` event. */
function loadMusicKit(): Promise<MusicKitGlobal> {
  const w = window as MusicKitWindow;
  if (w.MusicKit) return Promise.resolve(w.MusicKit);
  loading ??= new Promise<MusicKitGlobal>((resolve, reject) => {
    const failed = () => {
      loading = undefined;
      reject(new Error("Couldn't load Apple Music. Check your connection and try again."));
    };
    document.addEventListener(
      'musickitloaded',
      () => {
        if (w.MusicKit) resolve(w.MusicKit);
        else failed();
      },
      { once: true },
    );
    const script = document.createElement('script');
    script.src = MUSICKIT_SRC;
    script.async = true;
    script.onerror = failed;
    document.head.appendChild(script);
  });
  return loading;
}

/**
 * Connects Apple Music: developer token from the API, MusicKit's sign-in, then the Music User Token to the API.
 * `cancelled` when the fan backs out of Apple's sign-in (MusicKit rejects, or gives no token).
 */
export function connectAppleMusic(api: Api): Promise<ConnectResult | 'cancelled'> {
  return connectAppleMusicWith(api, async (developerToken) => {
    // A failure to load or configure MusicKit is an error to show; its message is what the flow reports.
    const MusicKit = await loadMusicKit();
    await MusicKit.configure({ developerToken, app: { name: 'FirstLine', build: '0.1.0' } });
    const music: MusicKitInstance = MusicKit.getInstance();
    try {
      return await music.authorize();
    } catch {
      return null; // the fan closed Apple's sign-in
    }
  });
}
