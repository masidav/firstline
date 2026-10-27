import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { ConcertSourcesScreen, linkLabel } from './ConcertSourcesScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const geese = { id: 'a1', name: 'Geese', spotify_artist_id: 'sp', status: 'active', tracked_at: '2026-10-01T00:00:00Z' };
const linked = { source: 'ticketmaster' as const, external_id: 'K8vZGeese1', matched_by: 'staff' as const, checked_at: '2026-10-27T00:00:00Z', synced_at: null };

beforeEach(() => jest.clearAllMocks());

test('staff find an artist, link it to a Ticketmaster attraction, and can remove the link', async () => {
  mockApi.GET.mockImplementation((path: string) =>
    Promise.resolve(path === '/artists' ? ok({ items: [geese], next_cursor: null }) : ok({ items: [] })),
  );
  mockApi.PUT.mockResolvedValue(ok(linked));
  mockApi.DELETE.mockResolvedValue(ok(undefined));
  render(<ConcertSourcesScreen />);

  fireEvent.press(await screen.findByRole('button', { name: 'Open Geese' }));
  expect(await screen.findByText('Ticketmaster: Not looked up yet')).toBeTruthy();
  fireEvent.changeText(screen.getByLabelText('Ticketmaster attraction id'), ' K8vZGeese1 ');
  fireEvent.press(screen.getByRole('button', { name: 'Link to Ticketmaster' }));
  expect(await screen.findByText('Linked. Its shows load within a few minutes.')).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/admin/artists/{artistId}/sources/{source}', {
    params: { path: { artistId: 'a1', source: 'ticketmaster' } },
    body: { external_id: 'K8vZGeese1' },
  });
  expect(screen.getByText('Ticketmaster: Linked by staff')).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Remove link' }));
  await waitFor(() => expect(mockApi.DELETE).toHaveBeenCalled());
  expect(await screen.findByText('Link removed. Shows already loaded stay.')).toBeTruthy();
});

test('link labels', () => {
  expect(linkLabel(undefined)).toBe('Not looked up yet');
  expect(linkLabel({ ...linked, external_id: null, matched_by: null })).toMatch(/No match found/);
  expect(linkLabel({ ...linked, matched_by: 'spotify_link' as const })).toBe('Linked by its Spotify page');
});
