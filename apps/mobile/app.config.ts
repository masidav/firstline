import type { ConfigContext, ExpoConfig } from 'expo/config';

/**
 * Settings that depend on the environment, on top of app.json (the static part, which `eas init` and the Expo tools
 * edit). Every value here is public: it ends up in the app bundle. See README "Building the iOS app".
 */
export default ({ config }: ConfigContext): ExpoConfig => {
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

  return {
    ...config,
    name: config.name ?? 'Fandom Score',
    slug: config.slug ?? 'fandom-score',
    scheme: schemes,
    ios: { ...config.ios, infoPlist: { ...config.ios?.infoPlist, ...localNetworking } },
    extra: { ...config.extra, ...(projectId ? { eas: { ...(config.extra?.eas as object), projectId } } : {}) },
  };
};

/** Google's required redirect scheme for iOS clients: `123-abc.apps.googleusercontent.com` reversed. */
function reversed(clientId: string): string {
  const [id, ...domain] = clientId.split('.');
  return `${domain.reverse().join('.')}.${id}`;
}
