// @vitest-environment node
import { ApiError } from '../api/client';
import { SessionManager, type TokenStore } from './session';

// The same rules as the fan app's session tests (apps/mobile/src/auth/session.test.ts), for the dashboard's
// own SessionManager: it keeps the refresh token in a synchronous store and has no provider memory.

const BASE = 'http://api.test/v1';
const me = { id: '00000000-0000-0000-0000-000000000001', handle: 'promoter', email: 'p@example.test', created_at: '2026-10-18T00:00:00Z' };

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function session(access: string, refresh: string) {
  return { access_token: access, refresh_token: refresh, expires_in: 900, token_type: 'Bearer', is_new_user: false, user: me };
}

function memoryStore(initial: string | null = null): TokenStore & { value: string | null } {
  const store = {
    value: initial,
    get: () => store.value,
    set: (token: string) => {
      store.value = token;
    },
    clear: () => {
      store.value = null;
    },
  };
  return store;
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

test('restore without a stored token signs out without calling the API', async () => {
  const api = fakeApi({});
  const manager = new SessionManager(BASE, memoryStore(), api.fetchImpl);

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'signedOut' });
  expect(api.calls).toEqual([]);
});

test('restore resumes a stored session and keeps the rotated refresh token', async () => {
  const store = memoryStore('r1');
  const api = fakeApi({
    'POST /auth/refresh': async (request) => {
      expect(await request.json()).toEqual({ refresh_token: 'r1' });
      return json(200, session('a2', 'r2'));
    },
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'signedIn', me });
  expect(store.value).toBe('r2');
});

test('a new account needs a handle, then signs in', async () => {
  const store = memoryStore();
  const api = fakeApi({
    'POST /auth/sessions': async (request) => {
      const body = (await request.json()) as { handle?: string };
      return body.handle
        ? json(201, session('a1', 'r1'))
        : json(422, { code: 'validation_failed', errors: [{ field: 'handle', message: 'Required to create an account.' }] });
    },
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);

  const error = await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' }).catch((e: unknown) => e);
  expect(error).toBeInstanceOf(ApiError);
  expect((error as ApiError).fieldError('handle')).toBe('Required to create an account.');
  expect(manager.getState()).toEqual({ status: 'loading' });

  await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' }, 'promoter');
  expect(manager.getState()).toEqual({ status: 'signedIn', me });
  expect(store.value).toBe('r1');
});

test('the nonce the dashboard put in the identity token is sent along for the API to check', async () => {
  const api = fakeApi({
    'POST /auth/sessions': async (request) => {
      expect(await request.json()).toEqual({ provider: 'google', id_token: 't', nonce: 'n' });
      return json(200, session('a1', 'r1'));
    },
  });
  const manager = new SessionManager(BASE, memoryStore(), api.fetchImpl);

  await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' });

  expect(manager.getState()).toEqual({ status: 'signedIn', me });
});

test('calls carry the access token; an expired one is refreshed once for concurrent calls and they are retried', async () => {
  const store = memoryStore();
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
  await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' });

  const [first, second] = await Promise.all([manager.api.GET('/me'), manager.api.GET('/me')]);

  expect(first.response.status).toBe(200);
  expect(second.response.status).toBe(200);
  expect(api.calls.filter((call) => call === 'POST /auth/refresh')).toHaveLength(1);
  expect(store.value).toBe('r2');
});

test('a 401 before sign-in is not retried', async () => {
  const api = fakeApi({ 'GET /me': () => json(401, { code: 'unauthorized' }) });
  const manager = new SessionManager(BASE, memoryStore(), api.fetchImpl);

  const result = await manager.api.GET('/me');

  expect(result.response.status).toBe(401);
  expect(api.calls).toEqual(['GET /me']);
});

test('a rejected refresh ends the session', async () => {
  const store = memoryStore();
  const api = fakeApi({
    'POST /auth/sessions': () => json(200, session('a1', 'r1')),
    'POST /auth/refresh': () => json(401, { code: 'unauthorized' }),
    'GET /me': () => json(401, { code: 'unauthorized' }),
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);
  await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' });

  const result = await manager.api.GET('/me');

  expect(result.response.status).toBe(401);
  expect(manager.getState()).toEqual({ status: 'signedOut' });
  expect(store.value).toBeNull();
});

test('an unreachable API keeps the stored token for a retry', async () => {
  const store = memoryStore('r1');
  const manager = new SessionManager(BASE, store, async () => {
    throw new TypeError('Failed to fetch');
  });

  await manager.restore();

  expect(manager.getState()).toEqual({ status: 'unreachable' });
  expect(store.value).toBe('r1');
});

test('sign-out clears the session even if the API call fails, and tells subscribers', async () => {
  const store = memoryStore();
  const api = fakeApi({
    'POST /auth/sessions': () => json(200, session('a1', 'r1')),
    'DELETE /auth/sessions/current': () => json(500, { code: 'internal_error' }),
  });
  const manager = new SessionManager(BASE, store, api.fetchImpl);
  await manager.signIn({ provider: 'google', idToken: 't', nonce: 'n' });
  const seen: string[] = [];
  const unsubscribe = manager.subscribe(() => seen.push(manager.getState().status));

  await manager.signOut();
  unsubscribe();
  await manager.restore();

  expect(api.calls).toContain('DELETE /auth/sessions/current');
  expect(seen).toEqual(['signedOut']);
  expect(store.value).toBeNull();
});
