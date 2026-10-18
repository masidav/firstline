import * as SecureStore from 'expo-secure-store';

import { TOKEN_KEY, type TokenStore } from './storage';

/** iOS Keychain / Android Keystore. Only readable by this app, and not restored onto another device. */
export const tokenStore: TokenStore = {
  get: () => SecureStore.getItemAsync(TOKEN_KEY),
  set: (refreshToken) =>
    SecureStore.setItemAsync(TOKEN_KEY, refreshToken, {
      keychainAccessible: SecureStore.AFTER_FIRST_UNLOCK_THIS_DEVICE_ONLY,
    }),
  clear: () => SecureStore.deleteItemAsync(TOKEN_KEY),
};
