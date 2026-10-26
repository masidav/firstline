import { asProvider, PROVIDER_KEY, type StoredSession, TOKEN_KEY, type TokenStore } from './storage';

/**
 * Web: localStorage. Any script running on the page can read it, so the site must not load third-party
 * scripts; revisit (e.g. an httpOnly cookie) before a public web launch.
 */
export const tokenStore: TokenStore = {
  async get() {
    try {
      const refreshToken = globalThis.localStorage?.getItem(TOKEN_KEY);
      return refreshToken ? { refreshToken, provider: asProvider(globalThis.localStorage?.getItem(PROVIDER_KEY)) } : null;
    } catch {
      return null;
    }
  },
  async set(session: StoredSession) {
    globalThis.localStorage?.setItem(TOKEN_KEY, session.refreshToken);
    if (session.provider) {
      globalThis.localStorage?.setItem(PROVIDER_KEY, session.provider);
    }
  },
  async clear() {
    globalThis.localStorage?.removeItem(TOKEN_KEY);
    globalThis.localStorage?.removeItem(PROVIDER_KEY);
  },
};
