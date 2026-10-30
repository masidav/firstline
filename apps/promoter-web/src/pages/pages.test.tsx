import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { fakeApi, organization, renderInOrganization } from '../test/render';
import { AllocationPage } from './AllocationPage';
import { MembersPage } from './MembersPage';
import { ShowPage } from './ShowPage';
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

const metrics = (extra: Record<string, unknown> = {}) => ({
  seats_offered: 3, registrations: 4, withdrawn: 0, eligible_registrations: 4, ineligible_attempts: 1, hub_registrations: 3,
  non_hub_registrations: 1, selected_fans: 4, selected_in_draw: 2, plus_ones: 0, seats_filled: 2, hub_seats_filled: 1, non_hub_seats_filled: 1,
  tickets_delivered: 0, returns: 2, seats_returned: 3, cancellations: 0, re_offers: 2, seats_re_offered: 2, seats_unfilled: 1,
  empty_seats: null, sell_through_percent: 66.7, scalping_complaints: null, ...extra,
});
const report = {
  event, generated_at: '2026-11-06T00:00:00Z', show_started: false, totals: metrics(),
  allocations: [{ allocation: allocation('selected'), metrics: metrics(), complaints: null }],
};

function api(role: 'owner' | 'admin' | 'analyst', allocationStatus = 'closed', draw = { status: 'not_run' }) {
  return fakeApi({
    'GET /organizations/{organizationId}/events/{eventId}/report': report,
    'PUT /organizations/{organizationId}/allocations/{allocationId}/complaints': {
      allocation_id: 'al-1', scalping_complaints: 2, note: 'Two fakes on a resale site', updated_by_handle: 'olivia', updated_at: '2026-11-06T00:00:00Z',
    },
    'GET /organizations/{organizationId}': organization(role),
    'GET /organizations/{organizationId}/events': { items: [{ event, allocations: [allocation(allocationStatus)] }] },
    'GET /organizations/{organizationId}/events/{eventId}': { event, allocations: [allocation(allocationStatus)] },
    'GET /artists/{artistId}': { id: 'ar-1', name: 'Lakeside' },
    'GET /organizations/{organizationId}/allocations/{allocationId}/registrations': {
      selection: null, items: [registration('fan_a', { plus_one: true }), registration('fan_b', { local: false }), registration('fan_c', { status: 'withdrawn' })],
    },
    'GET /organizations/{organizationId}/allocations/{allocationId}/selection': { allocation_id: 'al-1', queued_at: null, error: null, selection: null, ...draw },
    'GET /organizations/{organizationId}/allocations/{allocationId}/reservations': { items: [] },
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

test('the show page carries the case-study report, exports it as CSV, and lets admins record scalping complaints', async () => {
  const created = vi.fn((_: Blob) => 'blob:csv');
  Object.assign(URL, { createObjectURL: created, revokeObjectURL: vi.fn() });
  const fake = api('admin');
  renderInOrganization(<ShowPage />, 'shows/:eventId', '/o/org-1/shows/ev-1', fake);

  const table = await screen.findByRole('table', { name: 'Case-study report' });
  const row = (label: string) => within(table).getByRole('row', { name: new RegExp(`^${label} `) });
  expect(within(row('Seats offered')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Seats offered', '3', '3']);
  expect(within(row('Re-offers')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Re-offers', '2', '2']);
  expect(within(row('Sell-through %')).getAllByRole('cell').map((c) => c.textContent)).toEqual(['Sell-through %', '66.7%', '66.7%']);
  expect(within(row('Empty seats at show start')).getAllByRole('cell')[1]).toHaveTextContent('after the show');
  expect(within(row('Scalping complaints')).getAllByRole('cell')[1]).toHaveTextContent('not recorded');

  await userEvent.click(screen.getByRole('button', { name: 'Export report CSV' }));
  const csv = await created.mock.calls[0]![0].text();
  expect(csv.split('\r\n')[0]).toBe('metric,Fan allocation,Total');
  expect(csv).toContain('Seats filled,2,2');
  expect(csv).toContain('Scalping complaints,,');

  await userEvent.click(screen.getByRole('button', { name: 'Record complaints' }));
  await userEvent.clear(screen.getByLabelText('Scalping complaints for Fan allocation'));
  await userEvent.type(screen.getByLabelText('Scalping complaints for Fan allocation'), '2');
  await userEvent.type(screen.getByLabelText('Note'), 'Two fakes on a resale site');
  await userEvent.click(screen.getByRole('button', { name: 'Save complaints' }));
  await waitFor(() => expect(fake.PUT).toHaveBeenCalledWith('/organizations/{organizationId}/allocations/{allocationId}/complaints', {
    params: { path: { organizationId: 'org-1', allocationId: 'al-1' } }, body: { scalping_complaints: 2, note: 'Two fakes on a resale site' },
  }));
});

test('analysts read the report without the complaints controls', async () => {
  renderInOrganization(<ShowPage />, 'shows/:eventId', '/o/org-1/shows/ev-1', api('analyst'));

  await screen.findByRole('table', { name: 'Case-study report' });
  expect(screen.queryByRole('button', { name: 'Record complaints' })).not.toBeInTheDocument();
});
