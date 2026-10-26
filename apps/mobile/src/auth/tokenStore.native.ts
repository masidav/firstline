import * as SecureStore from 'expo-secure-store';

import { asProvider, PROVIDER_KEY, type StoredSession, TOKEN_KEY, type TokenStore } from './storage';

const options = { keychainAccessible: SecureStore.AFTER_FIRST_UNLOCK_THIS_DEVICE_ONLY };

/** iOS Keychain / Android Keystore. Only readable by this app, and not restored onto another device. */
export const tokenStore: TokenStore = {
  async get() {
    const refreshToken = await SecureStore.getItemAsync(TOKEN_KEY);
    return refreshToken ? { refreshToken, provider: asProvider(await SecureStore.getItemAsync(PROVIDER_KEY)) } : null;
  },
  async set(session: StoredSession) {
    await SecureStore.setItemAsync(TOKEN_KEY, session.refreshToken, options);
    if (session.provider) {
      await SecureStore.setItemAsync(PROVIDER_KEY, session.provider, options);
    }
  },
  async clear() {
    await SecureStore.deleteItemAsync(TOKEN_KEY);
    await SecureStore.deleteItemAsync(PROVIDER_KEY);
  },
};
