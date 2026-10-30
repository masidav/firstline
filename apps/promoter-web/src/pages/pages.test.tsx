import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { fakeApi, organization, renderInOrganization } from '../test/render';
import { AllocationPage } from './AllocationPage';
import { MembersPage } from './MembersPage';
import { ShowsPage } from './ShowsPage';

const event = {
  id: 'ev-1', artist_id: 'ar-1', name: 'Lakeside at Metro', venue: 'Metro', city_id: 'us-il-chicago', city: 'Chicago',
  region: 'IL', country: 'US', time_zone: 'America/Chicago', starts_at: '2026-11-28T02:00:00Z', tickets_available: true,
  source: null, ticket_url: null, status: 'scheduled', time_tba: false, public_sale_starts_at: null, public_sale_ends_at: null, presales: [],
};
const allocation = (status: string) => ({
  id: 'al-1', event_id: 'ev-1', name: 'Fan allocation', tickets: 2, face_value_cents: 4500, currency: 'USD', min_raw_score: 1.5,
  registration_opens_at: '2026-11-01T15:00:00Z', registration_closes_at: '2026-11-05T15:00:00Z', status, delivery_hours_before_show: 24,
});
const registration = (handle: string, extra: Record<string, unknown> = {}) => ({
  handle, status: 'registered', local: true, plus_one: false, selection_rank: null, seats: null,
  registered_at: '2026-11-02T15:00:00Z', withdrawn_at: null, ...extra,
});

function api(role: 'owner' | 'admin' | 'analyst', allocationStatus = 'closed', draw = { status: 'not_run' }) {
  return fakeApi({
    'GET /organizations/{organizationId}': organization(role),
    'GET /organizations/{organizationId}/events': { items: [{ event, allocations: [allocation(allocationStatus)] }] },
    'GET /organizations/{organizationId}/events/{eventId}': { event, allocations: [allocation(allocationStatus)] },
    'GET /artists/{artistId}': { id: 'ar-1', name: 'Lakeside' },
    'GET /organizations/{organizationId}/allocations/{allocationId}/registrations': {
      selection: null, items: [registration('fan_a', { plus_one: true }), registration('fan_b', { local: false }), registration('fan_c', { status: 'withdrawn' })],
    },
    'GET /organizations/{organizationId}/allocations/{allocationId}/selection': { allocation_id: 'al-1', queued_at: null, error: null, selection: null, ...draw },
    'GET /organizations/{organizationId}/allocations/{allocationId}/reservations': { items: [] },
    'GET /organizations/{organizationId}/allocations/{allocationId}/eligibility-preview': {
      allocation_id: 'al-1', tickets: 2, threshold: 1.5, threshold_is_default: false, scored_fans: 12, excluded: 1,
      eligible: { threshold: 1.5, total: 7, local: 4, other: 3 },
      alternatives: [
        { threshold: 0.75, total: 10, local: 6, other: 4 },
        { threshold: 1.5, total: 7, local: 4, other: 3 },
        { threshold: 3, total: 3, local: 2, other: 1 },
        { threshold: 6, total: 1, local: 1, other: 0 },
      ],
      computed_at: '2026-10-30T12:00:00Z',
    },
    'POST /organizations/{organizationId}/allocations/{allocationId}/selection': { allocation_id: 'al-1', status: 'queued', queued_at: '2026-11-05T16:00:00Z', error: null, selection: null },
    'PUT /organizations/{organizationId}/members/{handle}': organization('owner'),
  });
}

test('the shows list names the artist, the place, the local time, and each allocation’s state; analysts get no New show', async () => {
  renderInOrganization(<ShowsPage />, '', '/o/org-1', api('analyst'));

  expect(await screen.findByRole('link', { name: 'Lakeside at Metro' })).toHaveAttribute('href', '/o/org-1/shows/ev-1');
  expect(await screen.findByText('Lakeside')).toBeInTheDocument();
  expect(screen.getByText('Metro, Chicago')).toBeInTheDocument();
  expect(screen.getByText(/Nov 27, 2026.*8:00\s?PM CST/)).toBeInTheDocument();
  expect(screen.getByText('Registration closed')).toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'New show' })).not.toBeInTheDocument();
});

test('an admin runs the draw once registration has closed; the page shows it queued', async () => {
  const fake = api('admin');
  renderInOrganization(<AllocationPage />, 'shows/:eventId/allocations/:allocationId', '/o/org-1/shows/ev-1/allocations/al-1', fake);

  const totals = await screen.findByLabelText('Registration totals');
  await waitFor(() => expect(within(totals).getAllByRole('strong').map((s) => s.textContent)).toEqual(['2', '1', '1', '3', '2']));
  expect(screen.getByText('@fan_a')).toBeInTheDocument();

  await userEvent.click(screen.getByRole('button', { name: 'Run the draw' }));

  expect(fake.POST).toHaveBeenCalledWith('/organizations/{organizationId}/allocations/{allocationId}/selection', {
    params: { path: { organizationId: 'org-1', allocationId: 'al-1' } },
  });
  expect(await screen.findByText(/The draw is queued/)).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Run the draw' })).not.toBeInTheDocument();
});

test('analysts see the draw but cannot run it, and registrations export as CSV', async () => {
  const created = vi.fn((_: Blob) => 'blob:csv');
  Object.assign(URL, { createObjectURL: created, revokeObjectURL: vi.fn() });
  renderInOrganization(<AllocationPage />, 'shows/:eventId/allocations/:allocationId', '/o/org-1/shows/ev-1/allocations/al-1', api('analyst'));

  expect(await screen.findByText('@fan_b')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Run the draw' })).not.toBeInTheDocument();

  await userEvent.click(screen.getByRole('button', { name: 'Export CSV' }));
  const csv = await created.mock.calls[0]![0].text();
  expect(csv.split('\r\n')[0]).toBe('handle,status,local,plus_one,rank,seats,registered_at,withdrawn_at,reservation');
  expect(csv).toContain('fan_a,registered,true,true,');
});

test('the eligible-fan preview shows counts against the tickets, local fans apart, and other thresholds; fans are never named', async () => {
  renderInOrganization(<AllocationPage />, 'shows/:eventId/allocations/:allocationId', '/o/org-1/shows/ev-1/allocations/al-1', api('admin', 'upcoming'));

  const totals = await screen.findByLabelText('Eligible fan totals');
  expect(within(totals).getAllByRole('strong').map((s) => s.textContent)).toEqual(['12', '7', '4', '3', '2']);
  expect(screen.getByText(/3\.5 eligible fans per ticket/)).toBeInTheDocument();
  expect(screen.getByText(/1 more have a score but can't register/)).toBeInTheDocument();
  expect(screen.getByText(/threshold can be changed until registration opens/)).toBeInTheDocument();

  const rows = within(screen.getByRole('table', { name: 'Other thresholds' })).getAllByRole('row').slice(1);
  expect(rows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
    ['0.75', '10', '6', '4', '5.0'],
    ['1.5 (current)', '7', '4', '3', '3.5'],
    ['3', '3', '2', '1', '1.5'],
    ['6', '1', '1', '0', '0.5'],
  ]);
  expect(within(totals).queryByText(/@/)).not.toBeInTheDocument();
});

test('owners add members by handle with a role; admins only see the list', async () => {
  const fake = api('owner');
  renderInOrganization(<MembersPage />, 'members', '/o/org-1/members', fake);

  await userEvent.type(await screen.findByLabelText('Add a member by handle'), '@new_analyst');
  await userEvent.click(screen.getByRole('button', { name: 'Add member' }));
  await waitFor(() => expect(fake.PUT).toHaveBeenCalledWith('/organizations/{organizationId}/members/{handle}', {
    params: { path: { organizationId: 'org-1', handle: 'new_analyst' } }, body: { role: 'analyst' },
  }));
});

test('admins see members without the controls', async () => {
  renderInOrganization(<MembersPage />, 'members', '/o/org-1/members', api('admin'));

  expect(await screen.findByText(/@adam/)).toBeInTheDocument();
  expect(screen.queryByLabelText('Add a member by handle')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
});
