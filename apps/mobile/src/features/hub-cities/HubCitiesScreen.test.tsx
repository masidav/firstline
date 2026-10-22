import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';

import { HubCitiesScreen } from './HubCitiesScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, code: string, detail: string) => ({ error: { code, detail }, response: { ok: false, status } });

const mockApi = { GET: jest.fn(), PUT: jest.fn() };
const mockSetMe = jest.fn();
const mockSession = { api: mockApi, setMe: mockSetMe };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession }),
}));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn(), back: jest.fn(), canGoBack: jest.fn(() => true) } }));

const chicago = { id: 'us-il-chicago', name: 'Chicago', region: 'IL', country: 'US' };
const toronto = { id: 'ca-on-toronto', name: 'Toronto', region: 'ON', country: 'CA' };
const newYork = { id: 'us-ny-new-york', name: 'New York', region: 'NY', country: 'US' };

const me = (hubCities: unknown[], changeAvailableAt: string | null = null) =>
  ({
    id: 'u-1',
    handle: 'fan',
    email: 'fan@example.test',
    created_at: '2026-10-21T12:00:00Z',
    hub_cities: hubCities,
    hub_cities_change_available_at: changeAvailableAt,
  }) as never;

/** GET /cities answers with the cities whose name contains the query. */
function serveCities() {
  mockApi.GET.mockImplementation(async (_path: string, options: { params: { query: { q: string } } }) =>
    ok({ items: [chicago, toronto, newYork].filter((c) => c.name.toLowerCase().includes(options.params.query.q.toLowerCase())) }),
  );
}

const { router } = jest.requireMock('expo-router');

beforeEach(() => {
  jest.clearAllMocks();
  serveCities();
});

test('a new fan searches, picks a primary city and a backup, and continues home', async () => {
  const saved = me([chicago, toronto]);
  mockApi.PUT.mockResolvedValue(ok(saved));
  render(<HubCitiesScreen me={me([])} />);

  expect(screen.getByText('Pick your hub city')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled();

  fireEvent.changeText(screen.getByLabelText('Search for your city'), 'chi');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Chicago, IL' }));
  fireEvent.changeText(screen.getByLabelText('Add a backup city'), 'tor');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Toronto, ON' }));

  expect(screen.getByText('Primary')).toBeTruthy();
  expect(screen.getByText('Backup')).toBeTruthy();
  // The primary city is set larger than its backups.
  const fontSize = (name: string) => StyleSheet.flatten(screen.getByText(name).props.style).fontSize;
  expect(fontSize('Chicago, IL')).toBeGreaterThan(fontSize('Toronto, ON'));
  fireEvent.press(screen.getByRole('button', { name: 'Continue' }));

  await waitFor(() => expect(router.replace).toHaveBeenCalledWith('/home'));
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/hub-cities', { body: { city_ids: ['us-il-chicago', 'ca-on-toronto'] } });
  expect(mockSetMe).toHaveBeenCalledWith(saved);
});

test('a backup can be made primary, and three is the most', async () => {
  render(<HubCitiesScreen me={me([chicago, toronto, newYork])} />);

  fireEvent.press(screen.getByRole('button', { name: 'Make New York, NY primary' }));

  const primary = screen.getAllByText(/, (IL|ON|NY)$/).map((node) => node.props.children);
  expect(primary[0]).toBe('New York, NY');
  expect(screen.queryByLabelText('Add a backup city')).toBeNull();
  expect(screen.getByText(/That's three/)).toBeTruthy();
});

test('changing hub cities warns about the weekly limit before saving', async () => {
  mockApi.PUT.mockResolvedValue(ok(me([toronto], '2026-10-28T12:00:00Z')));
  render(<HubCitiesScreen me={me([chicago, toronto])} />);

  expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  fireEvent.press(screen.getByRole('button', { name: 'Remove Chicago, IL' }));
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  expect(screen.getByText(/once a week/)).toBeTruthy();
  expect(mockApi.PUT).not.toHaveBeenCalled();
  fireEvent.press(screen.getByRole('button', { name: 'Save hub cities' }));

  await waitFor(() => expect(router.back).toHaveBeenCalled());
  expect(mockApi.PUT).toHaveBeenCalledWith('/me/hub-cities', { body: { city_ids: ['ca-on-toronto'] } });
});

test('a recent change locks editing until next week', () => {
  render(<HubCitiesScreen me={me([chicago], '2026-10-28T12:00:00Z')} />);

  expect(screen.getByText(/You can change them again on/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
  expect(screen.queryByLabelText('Add a backup city')).toBeNull();
});

test('the server refusing a change shows why', async () => {
  mockApi.PUT.mockResolvedValue(
    problem(429, 'hub_change_limit', 'Hub cities can be changed once a week. The next change is possible after 2026-10-28 12:00 UTC.'),
  );
  render(<HubCitiesScreen me={me([chicago])} />);

  fireEvent.changeText(screen.getByLabelText('Add a backup city'), 'tor');
  fireEvent.press(await screen.findByRole('button', { name: 'Add Toronto, ON' }));
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));
  fireEvent.press(screen.getByRole('button', { name: 'Save hub cities' }));

  expect(await screen.findByText(/next change is possible after 2026-10-28/)).toBeTruthy();
  expect(router.replace).not.toHaveBeenCalled();
});

test('a city not in the list says what is covered', async () => {
  render(<HubCitiesScreen me={me([])} />);

  fireEvent.changeText(screen.getByLabelText('Search for your city'), 'Atlantis');

  expect(await screen.findByText(/major US and Canadian cities/)).toBeTruthy();
});
