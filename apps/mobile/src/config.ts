// Settings. A hosted web build reads them at startup from /config.js, which the web server writes from its environment
// (infrastructure/web/), so one image serves beta and prod. Otherwise (local development, tests, the native app) they
// are build-time values: EXPO_PUBLIC_* are inlined into the bundle, so never put secrets in either. See .env.example.

/** What /config.js may set (window.__FANDOM_CONFIG__). Empty strings count as unset. */
interface RuntimeConfig {
  apiUrl?: string;
  devSignIn?: boolean;
  googleWebClientId?: string;
  spotifyClientId?: string;
}

const runtime: RuntimeConfig = (globalThis as { __FANDOM_CONFIG__?: RuntimeConfig }).__FANDOM_CONFIG__ ?? {};

export const config = {
  apiUrl: runtime.apiUrl || process.env.EXPO_PUBLIC_API_URL || 'http://localhost:5271/v1',
  devSignIn: runtime.devSignIn ?? process.env.EXPO_PUBLIC_DEV_SIGN_IN === 'true',
  /** Google OAuth "Web application" client; the API must list it in Auth__Google__ClientIds. Empty hides the button. */
  googleWebClientId: runtime.googleWebClientId || process.env.EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID || '',
  /** Google OAuth "iOS" client, for the native app. Its reversed form is also the app's URL scheme (app.json). */
  googleIosClientId: process.env.EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID ?? '',
  /** Must be the same app as the API's Spotify__ClientId. Not a secret. */
  spotifyClientId: runtime.spotifyClientId || process.env.EXPO_PUBLIC_SPOTIFY_CLIENT_ID || '',
};
