import { Platform } from 'react-native';

import type { Api } from '../../api/client';
import { APPLE_MUSIC_WEB_ONLY, connectAppleMusic } from './appleMusicConnect.native';
import { notConnected } from './musicConnect';

const mockKit = { authorizeAppleMusic: jest.fn(), isAppleMusicKitAvailable: jest.fn() };
jest.mock('../../../modules/apple-music-kit', () => ({
  authorizeAppleMusic: (...args: unknown[]) => mockKit.authorizeAppleMusic(...args),
  isAppleMusicKitAvailable: () => mockKit.isAppleMusicKitAvailable(),
}));

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), PUT: jest.fn() };
const api = mockApi as unknown as Api;
const connected = { ...notConnected('apple_music'), connected: true, sync_state: 'queued' as const };

beforeEach(() => {
  jest.clearAllMocks();
  Platform.OS = 'ios';
  mockKit.isAppleMusicKitAvailable.mockReturnValue(true);
  mockKit.authorizeAppleMusic.mockResolvedValue('music-user-token');
  mockApi.GET.mockResolvedValue(ok({ token: 'dev.token', expires_in: 3600 }));
  mockApi.PUT.mockResolvedValue(ok(connected));
});

test('iOS: the developer token goes to MusicKit and the Music User Token to the API', async () => {
  await expect(connectAppleMusic(api)).resolves.toEqual({ ok: true, status: connected });

  expect(mockApi.GET).toHaveBeenCalledWith('/me/music/apple_music/developer-token');
  expect(mockKit.authorizeAppleMusic).toHaveBeenCalledWith('dev.token');
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/music/{provider}', {
    params: { path: { provider: 'apple_music' } },
    body: { music_user_token: 'music-user-token' },
  });
});

test('not allowing Apple Music access cancels without calling the API', async () => {
  mockKit.authorizeAppleMusic.mockResolvedValue(null);

  await expect(connectAppleMusic(api)).resolves.toBe('cancelled');
  expect(mockApi.PUT).not.toHaveBeenCalled();
});

test('a token MusicKit refuses to issue is explained (no subscription)', async () => {
  mockKit.authorizeAppleMusic.mockRejectedValue(new Error('MusicKit.MusicTokenRequestError'));

  await expect(connectAppleMusic(api)).resolves.toMatchObject({ ok: false, message: expect.stringMatching(/Apple Music subscription/) });
  expect(mockApi.PUT).not.toHaveBeenCalled();
});

test('a server without Apple Music set up is explained before MusicKit runs', async () => {
  mockApi.GET.mockResolvedValue(problem(503, 'music_unavailable'));

  await expect(connectAppleMusic(api)).resolves.toMatchObject({ ok: false, message: expect.stringMatching(/Apple Music isn't available/) });
  expect(mockKit.authorizeAppleMusic).not.toHaveBeenCalled();
});

test('a token Apple rejects is explained from the field error', async () => {
  mockApi.PUT.mockResolvedValue({
    error: { code: 'validation_failed', errors: [{ field: 'music_user_token', message: 'Apple Music rejected the token.' }] },
    response: { ok: false, status: 422 },
  });

  await expect(connectAppleMusic(api)).resolves.toEqual({ ok: false, message: 'Apple Music rejected the token.' });
});

test('an iOS build without the module (Expo Go) says so', async () => {
  mockKit.isAppleMusicKitAvailable.mockReturnValue(false);

  await expect(connectAppleMusic(api)).resolves.toEqual({ ok: false, message: "This build can't connect Apple Music. Connect from the web app." });
  expect(mockApi.GET).not.toHaveBeenCalled();
});

test('Android points to the web app', async () => {
  Platform.OS = 'android';

  await expect(connectAppleMusic(api)).resolves.toEqual({ ok: false, message: APPLE_MUSIC_WEB_ONLY });
  expect(mockApi.GET).not.toHaveBeenCalled();
  expect(mockKit.authorizeAppleMusic).not.toHaveBeenCalled();
});
