// Build-time settings (VITE_* are inlined into the bundle, so never put secrets here). See the env example file.
export const config = {
  apiUrl: (import.meta.env.VITE_API_URL as string | undefined) ?? 'http://localhost:5271/v1',
  devSignIn: import.meta.env.VITE_DEV_SIGN_IN === 'true',
  /** Google OAuth web client; the API must list it in Auth__Google__ClientIds. Empty hides the button. */
  googleWebClientId: (import.meta.env.VITE_GOOGLE_WEB_CLIENT_ID as string | undefined) ?? '',
};
