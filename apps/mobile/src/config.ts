// Settings. A hosted web build reads them at startup from /config.js, which the web server writes from its environment
// (infrastructure/web/), so one image serves beta and prod (prod gets beta's digest). Otherwise (local development,
// tests, the native app) they are build-time values: EXPO_PUBLIC_* are inlined into the bundle, so never put secrets
// in either. See .env.example.

/** What /config.js may set (window.__FANDOM_CONFIG__). Empty strings count as unset. */
interface RuntimeConfig {
  apiUrl?: string;
  devSignIn?: boolean;
  googleWebClientId?: string;
  appleServicesId?: string;
  spotifyClientId?: string;
  tidalClientId?: string;
  appleMusicEnabled?: boolean;
}

const runtime: RuntimeConfig = (globalThis as { __FANDOM_CONFIG__?: RuntimeConfig }).__FANDOM_CONFIG__ ?? {};

export const config = {
  apiUrl: runtime.apiUrl || process.env.EXPO_PUBLIC_API_URL || 'http://localhost:5271/v1',
  devSignIn: runtime.devSignIn ?? process.env.EXPO_PUBLIC_DEV_SIGN_IN === 'true',
  /** Google OAuth "Web application" client; the API must list it in Auth__Google__ClientIds. Empty hides the button. */
  googleWebClientId: runtime.googleWebClientId || process.env.EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID || '',
  /** Google OAuth "iOS" client, for the native app. Its reversed form is also the app's URL scheme (app.json). */
  googleIosClientId: process.env.EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID ?? '',
  /**
   * Sign in with Apple on the web: the Services ID whose return URL is the API's `/v1/auth/apple/callback`; the API
   * must list it in Auth__Apple__ClientIds. Empty hides the button on the web (iOS uses the bundle ID instead).
   */
  appleServicesId: runtime.appleServicesId || process.env.EXPO_PUBLIC_APPLE_SERVICES_ID || '',
  /** Must be the same app as the API's Spotify__ClientId. Not a secret. */
  spotifyClientId: runtime.spotifyClientId || process.env.EXPO_PUBLIC_SPOTIFY_CLIENT_ID || '',
  /** Must be the same app as the API's MusicProviders__Tidal__ClientId. Not a secret. Empty hides "Connect Tidal". */
  tidalClientId: runtime.tidalClientId || process.env.EXPO_PUBLIC_TIDAL_CLIENT_ID || '',
  /** Show "Connect Apple Music"; the API must have its MusicKit key (MusicProviders__AppleMusic__*). */
  appleMusicEnabled: runtime.appleMusicEnabled ?? process.env.EXPO_PUBLIC_APPLE_MUSIC_ENABLED === 'true',
};
