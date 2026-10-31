import plist, { type PlistObject } from '@expo/plist';
import type { ConfigContext, ExpoConfig } from 'expo/config';
import { IOSConfig, withFinalizedMod } from 'expo/config-plugins';
import { readFileSync, writeFileSync } from 'node:fs';

/**
 * Settings that depend on the environment, on top of app.json (the static part, which `eas init` and the Expo tools
 * edit). Every value here is public: it ends up in the app bundle. See README "Building the iOS app".
 *
 * IOS_PERSONAL_TEAM=1 makes a build that a free Apple ID can sign in Xcode (README "On your own iPhone with a free
 * Apple ID"): a personal team gets no Sign in with Apple or push entitlement, so those are left out and the app hides
 * the Apple button, and the bundle ID gets a `.dev` suffix so the real one stays unregistered until the Developer
 * Program. APPLE_TEAM_ID (either kind of team) goes into the Xcode project so `expo run:ios` can sign.
 */
export default ({ config }: ConfigContext): ExpoConfig => {
  const personalTeam = process.env.IOS_PERSONAL_TEAM === '1';
  const schemes = [...(Array.isArray(config.scheme) ? config.scheme : [config.scheme ?? 'fandomscore'])];

  // Sign in with Google on the phone comes back on the iOS client's reversed ID (googleSignIn.native.ts).
  const googleIosClientId = process.env.EXPO_PUBLIC_GOOGLE_IOS_CLIENT_ID;
  if (googleIosClientId) schemes.push(reversed(googleIosClientId));

  // A development build against an API on the local network (http://192.168.x.x) needs the App Transport Security
  // exception for local networking. Release builds point at https and get none.
  const apiUrl = process.env.EXPO_PUBLIC_API_URL ?? '';
  const localNetworking = apiUrl.startsWith('http://') ? { NSAppTransportSecurity: { NSAllowsLocalNetworking: true } } : {};

  // `eas init` records the project id in app.json (extra.eas.projectId); EAS_PROJECT_ID is for builds made elsewhere.
  const projectId = process.env.EAS_PROJECT_ID ?? (config.extra?.eas as { projectId?: string } | undefined)?.projectId;

  const expo: ExpoConfig = {
    ...config,
    name: config.name ?? 'FirstLine',
    slug: config.slug ?? 'fandom-score',
    scheme: schemes,
    ios: {
      ...config.ios,
      ...(personalTeam ? { bundleIdentifier: `${config.ios?.bundleIdentifier}.dev`, usesAppleSignIn: false } : {}),
      ...(process.env.APPLE_TEAM_ID ? { appleTeamId: process.env.APPLE_TEAM_ID } : {}),
      infoPlist: { ...config.ios?.infoPlist, ...localNetworking },
    },
    extra: {
      ...config.extra,
      ...(projectId ? { eas: { ...(config.extra?.eas as object), projectId } } : {}),
      /** Read by appleSignIn.ts: false hides the button in a build without the entitlement. */
      appleSignIn: !personalTeam,
    },
  };
  return personalTeam ? withoutTeamEntitlements(expo) : expo;
};

/**
 * Removes the entitlements a personal team can't be provisioned for. The installed modules' config plugins add them
 * unconditionally and run after anything configured here, so this runs once every other mod has written the file.
 */
function withoutTeamEntitlements(config: ExpoConfig): ExpoConfig {
  return withFinalizedMod(config, [
    'ios',
    (mod) => {
      const path = IOSConfig.Entitlements.getEntitlementsPath(mod.modRequest.projectRoot);
      if (path) {
        const { 'aps-environment': _push, 'com.apple.developer.applesignin': _apple, ...rest } = plist.parse(
          readFileSync(path, 'utf8'),
        ) as PlistObject;
        writeFileSync(path, plist.build(rest));
      }
      return mod;
    },
  ]);
}

/** Google's required redirect scheme for iOS clients: `123-abc.apps.googleusercontent.com` reversed. */
function reversed(clientId: string): string {
  const [id, ...domain] = clientId.split('.');
  return `${domain.reverse().join('.')}.${id}`;
}

