import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ShowScreen } from './ShowScreen';
import { type Standing, standingMessage } from './standing';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn' } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const standing = (likely: boolean, extra: Partial<Standing> = {}): Standing => ({
  rank: 4, registrants: 40, top_percent: 10, seats_ahead: 5, seats_needed: 1, tickets: 100, margin_seats: 25,
  local: true, likely, ...extra,
});

const detail = {
  event: { id: 'ev-1', artist_id: 'ar-1', name: 'Geese at the Metro', venue: 'Metro', city_id: 'us-il-chicago', city: 'Chicago',
           region: 'IL', country: 'US', starts_at: '2026-12-01T02:00:00Z', tickets_available: true, source: null, ticket_url: null, status: 'scheduled', time_tba: false, public_sale_starts_at: null, public_sale_ends_at: null, presales: [] },
  allocations: [{ id: 'al-1', event_id: 'ev-1', name: 'Fan allocation', tickets: 100, face_value_cents: 4500, currency: 'USD',
                  min_raw_score: 5.56, registration_opens_at: '2026-10-20T00:00:00Z', registration_closes_at: '2026-11-20T00:00:00Z',
                  status: 'open', published_at: '2026-10-19T00:00:00Z', cancelled_at: null, selected_at: null, seats_left: null }],
};

const eligibility = (overrides: Record<string, unknown>) => ({
  allocation_id: 'al-1', eligible: true, reason: null, min_raw_score: 5.56, raw_score: 40, percentile: 90, is_estimated: false,
  hub_match: 1, registration: null, standing: standing(true), ...overrides,
});

beforeEach(() => jest.clearAllMocks());

test('says "most likely" only when the API says the fan is inside the margin', () => {
  expect(standingMessage(standing(true)).headline).toBe("You'll most likely get a ticket!");
  const unsure = standingMessage(standing(false));
  expect(unsure.headline).toBe("You're in the top 10% of fans registered so far.");
  expect(unsure.detail).toMatch(/isn't a sure thing yet/);
  expect(standingMessage(standing(false, { local: false })).detail).toMatch(/hub get first pick/);
});

test('an eligible fan sees their chances and registers with a +1', async () => {
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/events/{eventId}' ? detail : eligibility({}))),
  );
  mockApi.PUT.mockResolvedValue(ok({}, 201));
  render(<ShowScreen eventId="ev-1" />);

  expect(await screen.findByText("You'll most likely get a ticket!")).toBeTruthy();
  expect(screen.getByText(/You're #4 of 40 fans registered so far \(top 10%\)/)).toBeTruthy();

  fireEvent.press(screen.getByLabelText('Bring a +1'));
  await waitFor(() =>
    expect(mockApi.GET).toHaveBeenCalledWith('/allocations/{allocationId}/eligibility', {
      params: { path: { allocationId: 'al-1' }, query: { plus_one: true } },
    }),
  );
  fireEvent.press(await screen.findByLabelText('Register'));
  await waitFor(() =>
    expect(mockApi.PUT).toHaveBeenCalledWith('/allocations/{allocationId}/registration', {
      params: { path: { allocationId: 'al-1' } },
      body: { plus_one: true },
    }),
  );
});

test('a fan outside the margin is told where they stand without a promise', async () => {
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/events/{eventId}' ? detail : eligibility({ standing: standing(false) }))),
  );
  render(<ShowScreen eventId="ev-1" />);

  expect(await screen.findByText("You're in the top 10% of fans registered so far.")).toBeTruthy();
  expect(screen.queryByText("You'll most likely get a ticket!")).toBeNull();
});

test('a selected fan sees their ticket and can name their +1', async () => {
  const registration = { id: 'r-1', allocation_id: 'al-1', event_id: 'ev-1', hub_city_ids: ['us-il-chicago'], hub_match: 1, local: true,
                         percentile: 90, raw_score: 40, is_estimated: false, registered_at: '2026-10-21T00:00:00Z', withdrawn_at: null,
                         status: 'selected', plus_one: true, guest: null, selection_rank: 3, seats: 2 };
  const selected = { ...detail, allocations: [{ ...detail.allocations[0], status: 'selected' }] };
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/events/{eventId}' ? selected : eligibility({ eligible: true, registration, standing: null }))),
  );
  mockApi.PUT.mockResolvedValue(ok(registration));
  render(<ShowScreen eventId="ev-1" />);

  expect(await screen.findByText('You got tickets for you and your +1!')).toBeTruthy();
  fireEvent.changeText(screen.getByLabelText("Your +1's handle"), 'friend');
  fireEvent.press(screen.getByLabelText('Name my +1'));
  await waitFor(() =>
    expect(mockApi.PUT).toHaveBeenCalledWith('/allocations/{allocationId}/registration/guest', {
      params: { path: { allocationId: 'al-1' } },
      body: { handle: 'friend' },
    }),
  );
});

test('a fan below the minimum is told why and has no Register button', async () => {
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/events/{eventId}' ? detail : eligibility({ eligible: false, reason: 'below_threshold', standing: null }))),
  );
  render(<ShowScreen eventId="ev-1" />);

  expect(await screen.findByText(/below this allocation's minimum/)).toBeTruthy();
  expect(screen.queryByLabelText('Register')).toBeNull();
});

test('a fan named as someone\'s +1 is told why and can decline it, then register', async () => {
  let named = true;
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(ok(path === '/events/{eventId}' ? detail : named
      ? eligibility({ eligible: false, reason: 'named_as_guest', standing: null })
      : eligibility({}))),
  );
  mockApi.POST.mockImplementation(() => {
    named = false;
    return Promise.resolve(ok({ declined: 1 }));
  });
  render(<ShowScreen eventId="ev-1" />);

  expect(await screen.findByText(/named you as their \+1/)).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Decline the +1 invite' }));
  await waitFor(() =>
    expect(mockApi.POST).toHaveBeenCalledWith('/events/{eventId}/guest-invite/decline', { params: { path: { eventId: 'ev-1' } } }),
  );
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Decline the +1 invite' })).toBeNull());
});
