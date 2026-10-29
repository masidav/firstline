/**
 * @jest-environment node
 */
import { ApiError } from '../api/client';
import { SessionManager } from './session';
import { memoryTokenStore } from './storage';

const BASE = 'http://api.test/v1';
const me = { id: '00000000-0000-0000-0000-000000000001', handle: 'alice', email: 'alice@example.test', created_at: '2026-10-18T00:00:00Z' };

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function session(access: string, refresh: string, isNewUser = false) {
  return { access_token: access, refresh_token: refresh, expires_in: 900, token_type: 'Bearer', is_new_user: isNewUser, user: me };
}

/** A scripted API: routes keyed by "METHOD /path"; records every call. */
function fakeApi(routes: Record<string, (request: Request) => Response | Promise<Response>>) {
  const calls: string[] = [];
  const fetchImpl = async (request: Request) => {
    const key = `${request.method} ${new URL(request.url).pathname.replace('/v1', '')}`;
    calls.push(key);
    const route = routes[key];
    return route ? route(request) : json(404, { code: 'not_found' });
  };
  return { fetchImpl, calls };
}

test('restore without a stored session signs out without calling the API', async () => {
  const api = fakeApi({});
  const manager = new SessionManager(BASE, memoryTokenStore(), api.fetchImpl);

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'signedOut' });
  expect(api.calls).toEqual([]);
});

test('restore resumes a stored session and stores the rotated refresh token', async () => {
  const store = memoryTokenStore('r1');
  const api = fakeApi({
    'POST /auth/refresh': async (request) => {
      expect(await request.json()).toEqual({ refresh_token: 'r1' });
      return json(200, session('a2', 'r2'));
    },
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'signedIn', me });
  expect(store.value).toEqual({ refreshToken: 'r2', provider: 'google' });
});

test('a new account needs a handle, then signs in', async () => {
  const store = memoryTokenStore();
  const api = fakeApi({
    'POST /auth/sessions': async (request) => {
      const body = (await request.json()) as { handle?: string };
      return body.handle
        ? json(201, session('a1', 'r1', true))
        : json(422, { code: 'validation_failed', errors: [{ field: 'handle', message: 'Required to create an account.' }] });
    },
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);

  const error = await manager.signIn({ provider: 'google', idToken: 'dev:alice', nonce: 'n' }).catch((e: unknown) => e);
  expect(error).toBeInstanceOf(ApiError);
  expect((error as ApiError).fieldError('handle')).toBe('Required to create an account.');

  await expect(manager.signIn({ provider: 'google', idToken: 'dev:alice', nonce: 'n' }, 'alice')).resolves.toEqual({ isNewUser: true });
  expect(manager.getState()).toEqual({ status: 'signedIn', me });
  expect(manager.provider).toBe('google');
  expect(store.value).toEqual({ refreshToken: 'r1', provider: 'google' });
});

test('the nonce the app put in the identity token is sent along for the API to check', async () => {
  const api = fakeApi({
    'POST /auth/sessions': async (request) => {
      expect(await request.json()).toEqual({ provider: 'apple', id_token: 't', nonce: 'n' });
      return json(200, session('a1', 'r1'));
    },
  });
  const store = memoryTokenStore();
  const manager = new SessionManager(BASE, store, api.fetchImpl);

  await manager.signIn({ provider: 'apple', idToken: 't', nonce: 'n' });

  expect(manager.provider).toBe('apple');
  expect(store.value).toEqual({ refreshToken: 'r1', provider: 'apple' });
});

test('an expired access token is refreshed once for concurrent calls, and the calls are retried', async () => {
  const store = memoryTokenStore();
  const api = fakeApi({
    'POST /auth/sessions': () => json(200, session('a1', 'r1')),
    'POST /auth/refresh': async () => {
      await new Promise((resolve) => setTimeout(resolve, 10));
      return json(200, session('a2', 'r2'));
    },
    'GET /me': (request) =>
      request.headers.get('Authorization') === 'Bearer a2' ? json(200, me) : json(401, { code: 'unauthorized' }),
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);
  await manager.signIn({ provider: 'google', idToken: 'dev:alice', nonce: 'n' });

  const [first, second] = await Promise.all([manager.api.GET('/me'), manager.api.GET('/me')]);

  expect(first.response.status).toBe(200);
  expect(second.response.status).toBe(200);
  expect(api.calls.filter((call) => call === 'POST /auth/refresh')).toHaveLength(1);
  expect(store.value).toEqual({ refreshToken: 'r2', provider: 'google' });
});

test('a rejected refresh ends the session', async () => {
  const store = memoryTokenStore();
  const api = fakeApi({
    'POST /auth/sessions': () => json(200, session('a1', 'r1')),
    'POST /auth/refresh': () => json(401, { code: 'unauthorized' }),
    'GET /me': () => json(401, { code: 'unauthorized' }),
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);
  await manager.signIn({ provider: 'google', idToken: 'dev:alice', nonce: 'n' });

  const result = await manager.api.GET('/me');

  expect(result.response.status).toBe(401);
  expect(manager.getState()).toEqual({ status: 'signedOut' });
  expect(store.value).toBeNull();
});

test('an unreachable API keeps the stored session for a retry', async () => {
  const store = memoryTokenStore('r1');
  const manager = new SessionManager(BASE, store, async () => {
    throw new TypeError('Network request failed');
  });

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'unreachable' });
  expect(store.value).toEqual({ refreshToken: 'r1', provider: 'google' });
});

test('sign-out clears the session even if the API call fails', async () => {
  const store = memoryTokenStore();
  const api = fakeApi({
    'POST /auth/sessions': () => json(200, session('a1', 'r1')),
    'DELETE /auth/sessions/current': () => json(500, { code: 'internal_error' }),
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);
  await manager.signIn({ provider: 'google', idToken: 'dev:alice', nonce: 'n' });

  await manager.signOut();

  expect(api.calls).toContain('DELETE /auth/sessions/current');
  expect(manager.getState()).toEqual({ status: 'signedOut' });
  expect(store.value).toBeNull();
});
