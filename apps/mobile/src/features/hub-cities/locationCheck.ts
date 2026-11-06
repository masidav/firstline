import * as Location from 'expo-location';

import { type Api, type components, unwrap } from '../../api/client';

// Optional location checks (POST /me/location-checks): confirming the fan is near a hub city at the moments the API
// lists (choosing hub cities, opening a show's standing, registering). Sharing is the fan's choice and never blocks
// anything: everything here answers null instead of throwing. The position is sent once and never stored; the API
// keeps only the nearest city and rounded distances (openapi.yaml). On the web this is the browser's geolocation.

export type LocationCheck = components['schemas']['LocationCheck'];
export type LocationCheckKind = 'hub_set' | 'eligibility' | 'registration';

/** True when the fan already allowed location, so a check can run without a prompt (the show screen never prompts). */
export async function hasLocationPermission(): Promise<boolean> {
  try {
    return (await Location.getForegroundPermissionsAsync()).granted;
  } catch {
    return false;
  }
}

/**
 * Asks for foreground permission (the system prompt, the first time), reads one position, and records the check.
 * Null when the fan declined, the platform has no location, or anything else failed; nothing is thrown.
 */
export async function recordLocationCheck(
  api: Api,
  options: { kind: LocationCheckKind; allocation_id?: string },
): Promise<LocationCheck | null> {
  try {
    const permission = await Location.requestForegroundPermissionsAsync();
    if (!permission.granted) return null;
    const { coords } = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.Balanced });
    return unwrap(
      await api.POST('/me/location-checks', {
        body: {
          kind: options.kind,
          ...(options.allocation_id ? { allocation_id: options.allocation_id } : {}),
          latitude: coords.latitude,
          longitude: coords.longitude,
          accuracy_m: coords.accuracy ?? null,
        },
      }),
    );
  } catch {
    return null;
  }
}
