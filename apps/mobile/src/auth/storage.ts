/**
 * What survives between app launches: the refresh token and which provider signed in (so account deletion can
 * ask Apple for a fresh code). The access token is kept in memory only.
 * Platform implementations: tokenStore.ts (web) and tokenStore.native.ts (iOS/Android).
 */
export type Provider = 'apple' | 'google';

export interface StoredSession {
  refreshToken: string;
  /** Null for sessions stored before the provider was recorded. */
  provider: Provider | null;
}

export interface TokenStore {
  get(): Promise<StoredSession | null>;
  set(session: StoredSession): Promise<void>;
  clear(): Promise<void>;
}

export const TOKEN_KEY = 'fandom-score.refresh-token';
export const PROVIDER_KEY = 'fandom-score.sign-in-provider';

export function asProvider(value: string | null | undefined): Provider | null {
  return value === 'apple' || value === 'google' ? value : null;
}

/** For tests. */
export function memoryTokenStore(initial: string | null = null): TokenStore & { value: StoredSession | null } {
  const store = {
    value: (initial === null ? null : { refreshToken: initial, provider: 'google' }) as StoredSession | null,
    async get() {
      return store.value;
    },
    async set(session: StoredSession) {
      store.value = session;
    },
    async clear() {
      store.value = null;
    },
  };
  return store;
}
