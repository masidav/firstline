import { Platform } from 'react-native';

import { linkFromResponse, registerForPush, resetPush, unregisterPush } from './push';

const mockNotifications = {
  getPermissionsAsync: jest.fn(),
  requestPermissionsAsync: jest.fn(),
  getExpoPushTokenAsync: jest.fn(),
  setNotificationChannelAsync: jest.fn(),
  AndroidImportance: { HIGH: 4 },
};
const mockDevice = { isDevice: true };
const mockConstants: { expoConfig: unknown; easConfig: unknown } = { expoConfig: { extra: { eas: { projectId: 'proj-1' } } }, easConfig: null };

jest.mock('expo-notifications', () => ({
  __esModule: true,
  getPermissionsAsync: (...a: unknown[]) => mockNotifications.getPermissionsAsync(...a),
  requestPermissionsAsync: (...a: unknown[]) => mockNotifications.requestPermissionsAsync(...a),
  getExpoPushTokenAsync: (...a: unknown[]) => mockNotifications.getExpoPushTokenAsync(...a),
  setNotificationChannelAsync: (...a: unknown[]) => mockNotifications.setNotificationChannelAsync(...a),
  AndroidImportance: { HIGH: 4 },
}));
jest.mock('expo-device', () => ({
  __esModule: true,
  get isDevice() {
    return mockDevice.isDevice;
  },
}));
jest.mock('expo-constants', () => ({
  __esModule: true,
  default: {
    get expoConfig() {
      return mockConstants.expoConfig;
    },
    get easConfig() {
      return mockConstants.easConfig;
    },
  },
}));

const api = {
  PUT: jest.fn(),
  DELETE: jest.fn(),
};
const token = 'ExponentPushToken[abcdefghij123]';

beforeEach(() => {
  jest.clearAllMocks();
  resetPush();
  Platform.OS = 'ios';
  mockDevice.isDevice = true;
  mockConstants.expoConfig = { extra: { eas: { projectId: 'proj-1' } } };
  mockNotifications.getExpoPushTokenAsync.mockResolvedValue({ data: token });
  api.PUT.mockResolvedValue({ data: { id: 'dev-1', platform: 'ios' } });
  api.DELETE.mockResolvedValue({});
});

test('asks once, registers the token, and unregisters on sign-out', async () => {
  mockNotifications.getPermissionsAsync.mockResolvedValue({ status: 'undetermined', canAskAgain: true });
  mockNotifications.requestPermissionsAsync.mockResolvedValue({ status: 'granted', canAskAgain: true });

  expect(await registerForPush(api as never, true)).toBe('registered');
  expect(mockNotifications.getExpoPushTokenAsync).toHaveBeenCalledWith({ projectId: 'proj-1' });
  expect(api.PUT).toHaveBeenCalledWith('/me/push-devices', { body: { token, platform: 'ios' } });

  await unregisterPush(api as never);
  expect(api.DELETE).toHaveBeenCalledWith('/me/push-devices/{deviceId}', { params: { path: { deviceId: 'dev-1' } } });
  await unregisterPush(api as never);
  expect(api.DELETE).toHaveBeenCalledTimes(1);
});

test('the quiet launch refresh never prompts', async () => {
  mockNotifications.getPermissionsAsync.mockResolvedValue({ status: 'undetermined', canAskAgain: true });
  expect(await registerForPush(api as never, false)).toBe('off');
  expect(mockNotifications.requestPermissionsAsync).not.toHaveBeenCalled();
  expect(api.PUT).not.toHaveBeenCalled();
});

test('a refusal in system settings, the web, a simulator, and a missing project id', async () => {
  mockNotifications.getPermissionsAsync.mockResolvedValue({ status: 'denied', canAskAgain: false });
  expect(await registerForPush(api as never, true)).toBe('denied');

  Platform.OS = 'web';
  expect(await registerForPush(api as never, true)).toBe('unsupported');
  Platform.OS = 'ios';
  mockDevice.isDevice = false;
  expect(await registerForPush(api as never, true)).toBe('unsupported');
  mockDevice.isDevice = true;
  mockConstants.expoConfig = { extra: {} };
  expect(await registerForPush(api as never, true)).toBe('not_configured');
});

test('Android gets a notification channel', async () => {
  Platform.OS = 'android';
  mockNotifications.getPermissionsAsync.mockResolvedValue({ status: 'granted', canAskAgain: true });
  expect(await registerForPush(api as never, false)).toBe('registered');
  expect(mockNotifications.setNotificationChannelAsync).toHaveBeenCalledWith('default', expect.objectContaining({ importance: 4 }));
  expect(api.PUT).toHaveBeenCalledWith('/me/push-devices', { body: { token, platform: 'android' } });
});

test('a tapped push opens its link, only an app path', () => {
  const response = (link: unknown) => ({ notification: { request: { content: { data: { link } } } } }) as never;
  expect(linkFromResponse(response('/tickets'))).toBe('/tickets');
  expect(linkFromResponse(response('https://evil.example'))).toBeNull();
  expect(linkFromResponse(null)).toBeNull();
});
