/**
 * @jest-environment node
 */
import type { Api } from '../../api/client';
import { notConnected } from './musicConnect';

// Jest resolves `./appleMusicConnect` to the native file (iOS platform), so the web file is loaded by its full name.
// The native flow has its own test (appleMusicConnect.native.test.ts).
const web = jest.requireActual('./appleMusicConnect.ts') as typeof import('./appleMusicConnect');

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string) => ({ error: { code }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), PUT: jest.fn() };
const api = mockApi as unknown as Api;
const connected = { ...notConnected('apple_music'), connected: true, sync_state: 'queued' as const };

// The browser pieces MusicKit JS needs, in place of a DOM: a window to hang MusicKit on, and a document that takes
// the script tag and fires `musickitloaded`.
const musicKit = { configure: jest.fn(), getInstance: jest.fn(), authorize: jest.fn() };
musicKit.getInstance.mockReturnValue({ authorize: (...args: unknown[]) => musicKit.authorize(...args) });
const listeners: Record<string, (() => void)[]> = {};
const scripts: { src?: string; async?: boolean; onerror?: () => void }[] = [];
const fakeWindow: { MusicKit?: unknown } = {};
const fakeDocument = {
  addEventListener: (name: string, listener: () => void) => (listeners[name] ??= []).push(listener),
  createElement: () => {
    const script = {};
    scripts.push(script);
    return script;
  },
  head: {
    appendChild: () => {
      // The CDN script has loaded: MusicKit is global and the event fires.
      fakeWindow.MusicKit = musicKit;
      listeners.musickitloaded?.forEach((listener) => listener());
    },
  },
};
Object.assign(globalThis, { window: fakeWindow, document: fakeDocument });

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.GET.mockResolvedValue(ok({ token: 'dev.token', expires_in: 3600 }));
  mockApi.PUT.mockResolvedValue(ok(connected));
});

test('loads MusicKit JS once, configures it with the developer token, and sends the Music User Token to the API', async () => {
  musicKit.authorize.mockResolvedValue('music-user-token');

  await expect(web.connectAppleMusic(api)).resolves.toEqual({ ok: true, status: connected });

  expect(scripts).toHaveLength(1);
  expect(scripts[0]?.src).toBe('https://js-cdn.music.apple.com/musickit/v3/musickit.js');
  expect(mockApi.GET).toHaveBeenCalledWith('/me/music/apple_music/developer-token');
  expect(musicKit.configure).toHaveBeenCalledWith({ developerToken: 'dev.token', app: { name: 'FirstLine', build: '0.1.0' } });
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/music/{provider}', {
    params: { path: { provider: 'apple_music' } },
    body: { music_user_token: 'music-user-token' },
  });

  // A second connect reuses the loaded MusicKit (a fresh developer token each time).
  await web.connectAppleMusic(api);
  expect(scripts).toHaveLength(1);
  expect(mockApi.GET).toHaveBeenCalledTimes(2);
});

test('closing Apple sign-in cancels without calling the API', async () => {
  musicKit.authorize.mockRejectedValue(new Error('Unauthorized'));
  await expect(web.connectAppleMusic(api)).resolves.toBe('cancelled');

  musicKit.authorize.mockResolvedValue(undefined);
  await expect(web.connectAppleMusic(api)).resolves.toBe('cancelled');

  expect(mockApi.PUT).not.toHaveBeenCalled();
});

test('a server without Apple Music set up is explained before MusicKit runs', async () => {
  mockApi.GET.mockResolvedValue(problem(503, 'music_unavailable'));

  await expect(web.connectAppleMusic(api)).resolves.toMatchObject({ ok: false, message: expect.stringMatching(/Apple Music isn't available/) });
  expect(musicKit.authorize).not.toHaveBeenCalled();
});

test('a token Apple rejects is explained from the field error', async () => {
  musicKit.authorize.mockResolvedValue('music-user-token');
  mockApi.PUT.mockResolvedValue({
    error: { code: 'validation_failed', errors: [{ field: 'music_user_token', message: 'Apple Music rejected the token.' }] },
    response: { ok: false, status: 422 },
  });

  await expect(web.connectAppleMusic(api)).resolves.toEqual({ ok: false, message: 'Apple Music rejected the token.' });
});

test('a MusicKit script that fails to load is an error, not a cancel', async () => {
  // A fresh page (a fresh module, so nothing is cached): no MusicKit yet, and the script tag's error handler fires
  // instead of `musickitloaded`.
  let fresh = web;
  jest.isolateModules(() => {
    fresh = jest.requireActual('./appleMusicConnect.ts') as typeof web;
  });
  fakeWindow.MusicKit = undefined;
  const appendChild = fakeDocument.head.appendChild;
  fakeDocument.head.appendChild = () => scripts[scripts.length - 1]?.onerror?.();
  try {
    await expect(fresh.connectAppleMusic(api)).resolves.toEqual({
      ok: false,
      message: "Couldn't load Apple Music. Check your connection and try again.",
    });
  } finally {
    fakeDocument.head.appendChild = appendChild;
  }
  expect(mockApi.PUT).not.toHaveBeenCalled();
});
