// Build-time settings (EXPO_PUBLIC_* are inlined into the bundle, so never put secrets here). See .env.example.
export const config = {
  apiUrl: process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5271/v1',
  devSignIn: process.env.EXPO_PUBLIC_DEV_SIGN_IN === 'true',
  /** Google OAuth "Web application" client; the API must list it in Auth__Google__ClientIds. Empty hides the button. */
  googleWebClientId: process.env.EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID ?? '',
  /** Google OAuth "iOS" client, for the native app. Its reversed form is also the app's URL scheme (app.json). */
  googleIosClientId: process.env.EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID ?? '',
  /** Must be the same app as the API's Spotify__ClientId. Not a secret. */
  spotifyClientId: process.env.EXPO_PUBLIC_SPOTIFY_CLIENT_ID ?? '',
};
