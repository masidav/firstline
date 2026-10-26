import Constants from 'expo-constants';
import * as Device from 'expo-device';
import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';

import type { Api } from '../../api/client';

/**
 * Push notifications on the phone (iOS and Android; not the web app). The app registers its Expo push token with the
 * API on every launch once the fan has allowed notifications (PUT /me/push-devices), and removes it on sign-out. The
 * worker then pushes every in-app notification to it. Needs an EAS project id in the app config (`eas init`).
 */
export type PushState = 'unsupported' | 'not_configured' | 'denied' | 'off' | 'registered';

let deviceId: string | null = null;

/** Push only exists in the phone app, on a real device (simulators have no push token). */
export function pushSupported(): boolean {
  return Platform.OS !== 'web' && Device.isDevice;
}

function projectId(): string | undefined {
  const extra = Constants.expoConfig?.extra as { eas?: { projectId?: string } } | undefined;
  return extra?.eas?.projectId ?? Constants.easConfig?.projectId;
}

/**
 * Registers this phone for pushes. With `ask`, shows the system prompt if the fan hasn't answered it yet; without,
 * registers only if notifications are already allowed (the quiet launch-time refresh). Never throws.
 */
export async function registerForPush(api: Api, ask: boolean): Promise<PushState> {
  if (!pushSupported()) return 'unsupported';
  const id = projectId();
  if (!id) return 'not_configured';
  try {
    let { status, canAskAgain } = await Notifications.getPermissionsAsync();
    if (status !== 'granted' && ask && canAskAgain) {
      ({ status, canAskAgain } = await Notifications.requestPermissionsAsync());
    }
    if (status !== 'granted') return canAskAgain ? 'off' : 'denied';

    if (Platform.OS === 'android') {
      await Notifications.setNotificationChannelAsync('default', {
        name: 'Fandom Score',
        importance: Notifications.AndroidImportance.HIGH,
      });
    }
    const token = (await Notifications.getExpoPushTokenAsync({ projectId: id })).data;
    const result = await api.PUT('/me/push-devices', {
      body: { token, platform: Platform.OS === 'ios' ? 'ios' : 'android' },
    });
    if (!result.data) return 'off';
    deviceId = result.data.id;
    return 'registered';
  } catch {
    return 'off';
  }
}

/** On sign-out: stop pushing to this phone. Best effort; never throws. */
export async function unregisterPush(api: Api): Promise<void> {
  const id = deviceId;
  deviceId = null;
  if (!id) return;
  try {
    await api.DELETE('/me/push-devices/{deviceId}', { params: { path: { deviceId: id } } });
  } catch {
    // The device stays registered; the next fan to sign in on this phone takes the token over.
  }
}

/** The app screen a tapped push opens (the notification's `link`), if it's one of ours. */
export function linkFromResponse(response: Notifications.NotificationResponse | null | undefined): string | null {
  const link = response?.notification.request.content.data?.link;
  return typeof link === 'string' && link.startsWith('/') ? link : null;
}

/** For tests. */
export function resetPush(): void {
  deviceId = null;
}
