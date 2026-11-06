/**
 * @jest-environment node
 */

// The module reads the native module once, at import, so each case loads it fresh with a different answer from
// expo-modules-core.
const mockRequireOptionalNativeModule = jest.fn();
jest.mock('expo-modules-core', () => ({
  ...jest.requireActual<object>('expo-modules-core'),
  requireOptionalNativeModule: (name: string) => mockRequireOptionalNativeModule(name),
}));

type AppleMusicKit = typeof import('./index');

function load(nativeModule: unknown): AppleMusicKit {
  mockRequireOptionalNativeModule.mockReturnValue(nativeModule);
  let loaded: AppleMusicKit | undefined;
  jest.isolateModules(() => {
    loaded = jest.requireActual<AppleMusicKit>('./index');
  });
  return loaded as AppleMusicKit;
}

beforeEach(() => jest.clearAllMocks());

test('without the native module (Android, web, Expo Go, Jest) nothing is authorized and the status is unavailable', async () => {
  const kit = load(null);

  expect(mockRequireOptionalNativeModule).toHaveBeenCalledWith('AppleMusicKit');
  expect(kit.isAppleMusicKitAvailable()).toBe(false);
  await expect(kit.authorizeAppleMusic('dev.token')).resolves.toBeNull();
  await expect(kit.appleMusicAuthorizationStatus()).resolves.toBe('unavailable');
});

test('with the native module, authorize and the status come from it', async () => {
  const native = {
    authorize: jest.fn().mockResolvedValue('music-user-token'),
    authorizationStatus: jest.fn().mockResolvedValue('authorized'),
  };
  const kit = load(native);

  expect(kit.isAppleMusicKitAvailable()).toBe(true);
  await expect(kit.authorizeAppleMusic('dev.token')).resolves.toBe('music-user-token');
  expect(native.authorize).toHaveBeenCalledWith('dev.token');
  await expect(kit.appleMusicAuthorizationStatus()).resolves.toBe('authorized');
});

test('a refusal from the fan (nil from Swift) is null, and a refusal from Apple rejects', async () => {
  const native = {
    authorize: jest.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('MusicKit: no subscription')),
    authorizationStatus: jest.fn().mockResolvedValue('denied'),
  };
  const kit = load(native);

  await expect(kit.authorizeAppleMusic('dev.token')).resolves.toBeNull();
  await expect(kit.authorizeAppleMusic('dev.token')).rejects.toThrow('MusicKit: no subscription');
  await expect(kit.appleMusicAuthorizationStatus()).resolves.toBe('denied');
});
