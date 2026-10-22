import { fireEvent, render, screen } from '@testing-library/react-native';

import { ArtistAlbumsScreen } from './ArtistAlbumsScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

const mockApi = { GET: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn' } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false } }));

const album = (id: string, title: string, year: string, type = 'album', logCount = 0) => ({
  id,
  artist: { id: 'ar-1', name: 'Radiohead', image_url: null },
  title,
  album_type: type,
  release_date: `${year}-01-01`,
  release_date_precision: 'year',
  cover_url: null,
  spotify_album_id: `sp-${id}`,
  delisted: false,
  community: { log_count: logCount, average_rating: null },
});

beforeEach(() => jest.clearAllMocks());

test('lists the artist albums newest first with their year and type', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [album('1', 'In Rainbows', '2007', 'album', 12), album('2', 'Supercollider', '2011', 'single')], next_cursor: null }));
  render(<ArtistAlbumsScreen artistId="ar-1" />);

  expect(await screen.findByRole('header', { name: 'Radiohead' })).toBeTruthy();
  expect(screen.getByText('2007 · Album')).toBeTruthy();
  expect(screen.getByText('2011 · Single')).toBeTruthy();
  expect(screen.getByText('12 logged')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/artists/{artistId}/albums', { params: { path: { artistId: 'ar-1' }, query: { limit: 100 } } });

  fireEvent.press(screen.getByLabelText('Open In Rainbows'));
  const { router } = jest.requireMock('expo-router');
  expect(router.push).toHaveBeenCalledWith('/album/1');
});

test('an artist with no albums yet says so', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], next_cursor: null }));
  render(<ArtistAlbumsScreen artistId="ar-1" />);

  expect(await screen.findByText(/No albums here yet/)).toBeTruthy();
});
