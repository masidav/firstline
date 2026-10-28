// Settings. A hosted build reads them at startup from /config.js, which the web server writes from its environment
// (infrastructure/web/), so one image serves beta and prod. Otherwise (local development, tests) they are build-time
// values: VITE_* are inlined into the bundle, so never put secrets in either. See the env example file.

/** What /config.js may set (window.__FANDOM_CONFIG__). Empty strings count as unset. */
interface RuntimeConfig {
  apiUrl?: string;
  devSignIn?: boolean;
  googleWebClientId?: string;
}

const runtime: RuntimeConfig = (globalThis as { __FANDOM_CONFIG__?: RuntimeConfig }).__FANDOM_CONFIG__ ?? {};

export const config = {
  apiUrl: runtime.apiUrl || (import.meta.env.VITE_API_URL as string | undefined) || 'http://localhost:5271/v1',
  devSignIn: runtime.devSignIn ?? import.meta.env.VITE_DEV_SIGN_IN === 'true',
  /** Google OAuth web client; the API must list it in Auth__Google__ClientIds. Empty hides the button. */
  googleWebClientId: runtime.googleWebClientId || (import.meta.env.VITE_GOOGLE_WEB_CLIENT_ID as string | undefined) || '',
};
