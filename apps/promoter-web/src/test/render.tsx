import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { vi } from 'vitest';

import { SessionProvider } from '../auth/SessionProvider';
import type { SessionManager } from '../auth/session';
import { OrganizationLayout } from '../pages/OrganizationLayout';

type Handler = (path: string, options?: { params?: { path?: Record<string, string> }; body?: unknown }) => unknown;

/** A fake API: `routes` maps "METHOD /path/template" to a response body, or to a function of the call. */
export function fakeApi(routes: Record<string, unknown>) {
  const call = (method: string) => vi.fn(async (path: string, options?: Parameters<Handler>[1]) => {
    const route = routes[`${method} ${path}`];
    if (route === undefined) return { error: { code: 'not_found' }, response: new Response(null, { status: 404 }) };
    const data = typeof route === 'function' ? (route as Handler)(path, options) : route;
    return { data, response: new Response(null, { status: method === 'POST' ? 202 : 200 }) };
  });
  return { GET: call('GET'), POST: call('POST'), PUT: call('PUT'), PATCH: call('PATCH'), DELETE: call('DELETE') };
}

export const organization = (role: 'owner' | 'admin' | 'analyst') => ({
  id: 'org-1', name: 'Metro Shows', kind: 'promoter', created_at: '2026-10-27T00:00:00Z', my_role: role,
  members: [
    { handle: 'olivia', display_name: null, role: 'owner', added_at: '2026-10-27T00:00:00Z' },
    { handle: 'adam', display_name: 'Adam', role: 'admin', added_at: '2026-10-27T00:00:00Z' },
  ],
});

/** Renders a page inside the organization layout at `url`, signed in, with the fake API. */
export function renderInOrganization(page: ReactElement, pattern: string, url: string, api: ReturnType<typeof fakeApi>) {
  const state = { status: 'signedIn', me: { handle: 'olivia' } }; // one object, as the real session keeps
  const manager = {
    api,
    getState: () => state,
    subscribe: () => () => {},
    signOut: vi.fn(),
  } as unknown as SessionManager;
  return render(
    <SessionProvider manager={manager}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/o/:organizationId" element={<OrganizationLayout />}>
            <Route path={pattern} element={page} />
          </Route>
        </Routes>
      </MemoryRouter>
    </SessionProvider>,
  );
}
