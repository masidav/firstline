/**
 * @jest-environment node
 */
import type { Api } from '../../api/client';
import { hasLocationPermission, recordLocationCheck } from './locationCheck';

const mockRequest = jest.fn();
const mockGet = jest.fn();
const mockPosition = jest.fn();

jest.mock('expo-location', () => ({
  Accuracy: { Balanced: 3 },
  requestForegroundPermissionsAsync: () => mockRequest(),
  getForegroundPermissionsAsync: () => mockGet(),
  getCurrentPositionAsync: (options: unknown) => mockPosition(options),
}));

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockPost = jest.fn();
const api = { POST: (...args: unknown[]) => mockPost(...args) } as unknown as Api;

const check = { recorded: true, hub_match: true, nearest_city: { id: 'us-il-chicago', name: 'Chicago' }, nearest_city_km: 0, nearest_hub_km: 0 };
const position = { coords: { latitude: 41.88, longitude: -87.63, accuracy: 25 } };

beforeEach(() => {
  jest.clearAllMocks();
  mockRequest.mockResolvedValue({ granted: true });
  mockPosition.mockResolvedValue(position);
  mockPost.mockResolvedValue(ok(check));
});

test('with permission, one balanced reading is posted with its accuracy and the check comes back', async () => {
  await expect(recordLocationCheck(api, { kind: 'hub_set' })).resolves.toEqual(check);

  expect(mockPosition).toHaveBeenCalledWith({ accuracy: 3 });
  expect(mockPost).toHaveBeenCalledWith('/me/location-checks', {
    body: { kind: 'hub_set', latitude: 41.88, longitude: -87.63, accuracy_m: 25 },
  });
  expect(mockPost.mock.calls[0][1].body).not.toHaveProperty('allocation_id');
});

test('an allocation is named for eligibility and registration checks', async () => {
  await recordLocationCheck(api, { kind: 'registration', allocation_id: 'al-1' });

  expect(mockPost).toHaveBeenCalledWith('/me/location-checks', {
    body: { kind: 'registration', allocation_id: 'al-1', latitude: 41.88, longitude: -87.63, accuracy_m: 25 },
  });
});

test('a missing accuracy is sent as null', async () => {
  mockPosition.mockResolvedValue({ coords: { latitude: 1, longitude: 2 } });

  await recordLocationCheck(api, { kind: 'hub_set' });

  expect(mockPost.mock.calls[0][1].body.accuracy_m).toBeNull();
});

test('declining the permission records nothing and reads no position', async () => {
  mockRequest.mockResolvedValue({ granted: false, status: 'denied' });

  await expect(recordLocationCheck(api, { kind: 'hub_set' })).resolves.toBeNull();

  expect(mockPosition).not.toHaveBeenCalled();
  expect(mockPost).not.toHaveBeenCalled();
});

test('a platform without location, a failed reading, or a failed request all end quietly in null', async () => {
  mockRequest.mockRejectedValueOnce(new Error('Location is not available'));
  await expect(recordLocationCheck(api, { kind: 'hub_set' })).resolves.toBeNull();

  mockPosition.mockRejectedValueOnce(new Error('timeout'));
  await expect(recordLocationCheck(api, { kind: 'hub_set' })).resolves.toBeNull();

  mockPost.mockResolvedValueOnce({ error: { code: 'validation_failed' }, response: { ok: false, status: 422 } });
  await expect(recordLocationCheck(api, { kind: 'eligibility', allocation_id: 'al-1' })).resolves.toBeNull();

  mockPost.mockRejectedValueOnce(new TypeError('Failed to fetch'));
  await expect(recordLocationCheck(api, { kind: 'hub_set' })).resolves.toBeNull();
});

test('hasLocationPermission reports a granted permission without asking, and false when the platform cannot say', async () => {
  mockGet.mockResolvedValueOnce({ granted: true });
  await expect(hasLocationPermission()).resolves.toBe(true);

  mockGet.mockResolvedValueOnce({ granted: false, status: 'undetermined' });
  await expect(hasLocationPermission()).resolves.toBe(false);

  mockGet.mockRejectedValueOnce(new Error('no module'));
  await expect(hasLocationPermission()).resolves.toBe(false);

  expect(mockRequest).not.toHaveBeenCalled();
});
