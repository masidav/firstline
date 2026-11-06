import { requireOptionalNativeModule } from 'expo-modules-core';

// Local Expo module wrapping Apple's MusicKit on iOS (ios/AppleMusicKitModule.swift; autolinked through
// expo-module.config.json). It exists only in an iOS build that includes native modules (a development, beta, or
// production build; not Expo Go, Android, the web, or Jest), so the native module is optional here and every
// function has an answer without it. Used by src/features/music/appleMusicConnect.native.ts.

/** MusicKit's authorization status, plus `unavailable` where the module doesn't exist. */
export type AppleMusicAuthorizationStatus = 'authorized' | 'denied' | 'restricted' | 'notDetermined' | 'unavailable';

/** The Swift module's surface (AsyncFunctions become promises). */
interface AppleMusicKitNativeModule {
  authorize(developerToken: string): Promise<string | null | undefined>;
  authorizationStatus(): Promise<Exclude<AppleMusicAuthorizationStatus, 'unavailable'>>;
}

const native = requireOptionalNativeModule<AppleMusicKitNativeModule>('AppleMusicKit');

/** Whether this build has the MusicKit module (iOS builds with native modules only). */
export function isAppleMusicKitAvailable(): boolean {
  return native !== null;
}

/**
 * Asks the fan to allow Apple Music access (the system prompt, the first time) and returns a Music User Token for
 * the given developer token. `null` when the fan didn't allow it, or where the module doesn't exist. Rejects when
 * Apple refuses to issue a token (no Apple Music subscription, a developer token Apple doesn't accept, no network).
 */
export async function authorizeAppleMusic(developerToken: string): Promise<string | null> {
  if (!native) return null;
  return (await native.authorize(developerToken)) ?? null;
}

/** What the fan has answered so far, without asking. */
export async function appleMusicAuthorizationStatus(): Promise<AppleMusicAuthorizationStatus> {
  if (!native) return 'unavailable';
  return native.authorizationStatus();
}
