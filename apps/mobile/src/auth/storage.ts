/**
 * Where the refresh token lives between app launches. The access token is kept in memory only.
 * Platform implementations: tokenStore.ts (web) and tokenStore.native.ts (iOS/Android).
 */
export interface TokenStore {
  get(): Promise<string | null>;
  set(refreshToken: string): Promise<void>;
  clear(): Promise<void>;
}

export const TOKEN_KEY = 'fandom-score.refresh-token';

/** For tests. */
export function memoryTokenStore(initial: string | null = null): TokenStore & { value: string | null } {
  const store = {
    value: initial,
    async get() {
      return store.value;
    },
    async set(refreshToken: string) {
      store.value = refreshToken;
    },
    async clear() {
      store.value = null;
    },
  };
  return store;
}
