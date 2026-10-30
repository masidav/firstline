import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import { configureAxe } from 'vitest-axe';

import { App } from './App';
import { SessionProvider } from './auth/SessionProvider';
import type { SessionManager } from './auth/session';
import { AllocationPage } from './pages/AllocationPage';
import { fakeApi, organization, renderInOrganization } from './test/render';
import { Button, ErrorText, Field, Select } from './ui/components';

// States the end-to-end scan (e2e/accessibility.spec.ts) never reaches: validation errors, a failed draw, the
// server being unreachable. Colour contrast needs a layout engine, so it is checked there, not in jsdom; `region`
// is off because these render fragments of a page, not a page with landmarks.
const axe = configureAxe({
  runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'] },
  rules: { 'color-contrast': { enabled: false }, region: { enabled: false }, 'page-has-heading-one': { enabled: false } },
});

test('form controls with errors, hints, and a busy button', async () => {
  const { container } = render(
    <form>
      <Field label="Tickets" type="number" value="0" error="Tickets: a whole number, at least 1." hint="One per selected fan." onChange={() => {}} />
      <Field label="Face value" value="" placeholder="45.00" aria-invalid onChange={() => {}} />
      <Select label="Role" value="analyst" onChange={() => {}}><option value="analyst">Analyst</option></Select>
      <Select label="Role of @adam" hideLabel value="admin" onChange={() => {}}><option value="admin">Admin</option></Select>
      <ErrorText>Choose when registration opens and closes.</ErrorText>
      <Button type="submit" busy>Create draft</Button>
    </form>,
  );
  expect(screen.getByLabelText('Tickets')).toHaveAccessibleDescription('One per selected fan.');
  expect(await axe(container)).toHaveNoViolations();
});

test('a failed draw, for an admin who can try again', async () => {
  const event = {
    id: 'ev-1', artist_id: 'ar-1', name: 'Lakeside at Metro', venue: 'Metro', city_id: 'us-il-chicago', city: 'Chicago', region: 'IL', country: 'US',
    time_zone: 'America/Chicago', starts_at: '2026-11-28T02:00:00Z', tickets_available: true, source: null, ticket_url: null, status: 'scheduled',
    time_tba: false, public_sale_starts_at: null, public_sale_ends_at: null, presales: [],
  };
  const allocation = {
    id: 'al-1', event_id: 'ev-1', name: 'Fan allocation', tickets: 2, face_value_cents: 4500, currency: 'USD', min_raw_score: 1.5,
    registration_opens_at: '2026-11-01T15:00:00Z', registration_closes_at: '2026-11-05T15:00:00Z', status: 'closed', delivery_hours_before_show: 24,
  };
  const { container } = renderInOrganization(
    <AllocationPage />, 'shows/:eventId/allocations/:allocationId', '/o/org-1/shows/ev-1/allocations/al-1',
    fakeApi({
      'GET /organizations/{organizationId}': organization('admin'),
      'GET /organizations/{organizationId}/events/{eventId}': { event, allocations: [allocation] },
      'GET /organizations/{organizationId}/allocations/{allocationId}/registrations': { selection: null, items: [] },
      'GET /organizations/{organizationId}/allocations/{allocationId}/selection': {
        allocation_id: 'al-1', status: 'failed', queued_at: '2026-11-05T16:00:00Z', error: 'worker timed out', selection: null,
      },
      'GET /organizations/{organizationId}/allocations/{allocationId}/reservations': { items: [] },
    }),
  );
  expect(await screen.findByRole('button', { name: 'Try the draw again' })).toBeInTheDocument();
  expect(screen.getByText(/worker timed out/)).toBeInTheDocument();
  expect(await axe(container)).toHaveNoViolations();
});

test('the server being unreachable', async () => {
  const state = { status: 'unreachable' };
  const manager = { api: fakeApi({}), getState: () => state, subscribe: () => () => {}, restore: vi.fn(), signOut: vi.fn() } as unknown as SessionManager;
  const { container } = render(
    <SessionProvider manager={manager}>
      <MemoryRouter initialEntries={['/']}><App /></MemoryRouter>
    </SessionProvider>,
  );
  expect(screen.getByRole('heading', { name: "Can't reach the server" })).toBeInTheDocument();
  expect(await axe(container)).toHaveNoViolations();
});
