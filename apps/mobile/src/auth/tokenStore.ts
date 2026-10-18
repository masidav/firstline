import { TOKEN_KEY, type TokenStore } from './storage';

/**
 * Web: localStorage. Any script running on the page can read it, so the site must not load third-party
 * scripts; revisit (e.g. an httpOnly cookie) before a public web launch.
 */
export const tokenStore: TokenStore = {
  async get() {
    try {
      return globalThis.localStorage?.getItem(TOKEN_KEY) ?? null;
    } catch {
      return null;
    }
  },
  async set(refreshToken) {
    globalThis.localStorage?.setItem(TOKEN_KEY, refreshToken);
  },
  async clear() {
    globalThis.localStorage?.removeItem(TOKEN_KEY);
  },
};
