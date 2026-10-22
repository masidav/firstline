import { fireEvent, render, screen } from '@testing-library/react-native';

import { Feed, stars } from './Feed';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

const artist = { id: 'a1', name: 'Geese', image_url: null };
const photo = {
  type: 'photo',
  id: 's1',
  at: '2026-10-21T12:00:00Z',
  user: { handle: 'fan_two', deleted: false, display_name: null },
  artist,
  photo: { kind: 'concert_photo', caption: 'Front row!', files: [{ id: 'f1', mime_type: 'image/jpeg', url: 'https://x/f1', redacted: false }] },
  log: null,
};
const log = {
  type: 'album_log',
  id: 'l1',
  at: '2026-10-21T11:00:00Z',
  user: { handle: 'fan_one', deleted: false, display_name: 'Ana' },
  artist,
  photo: null,
  log: { album: { id: 'al1', title: '3D Country', cover_url: null }, rating: 9, review: 'Loud.', review_truncated: false, is_relisten: true, listened_on: '2026-10-20' },
};

beforeEach(() => jest.clearAllMocks());

test('shows shared photos and album logs, and pages', async () => {
  mockApi.GET.mockResolvedValueOnce(ok({ items: [photo], next_cursor: 'c2' })).mockResolvedValueOnce(ok({ items: [log], next_cursor: null }));
  render(<Feed />);

  expect(await screen.findByText('Front row!')).toBeTruthy();
  expect(screen.getByLabelText("@fan_two's a concert photo for Geese")).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Show more' }));

  const logged = await screen.findByRole('button', { name: 'Ana logged 3D Country by Geese' });
  expect(screen.getByText('Geese · ★ 4.5')).toBeTruthy();
  expect(mockApi.GET).toHaveBeenLastCalledWith('/feed', { params: { query: { limit: 20, cursor: 'c2' } } });
  fireEvent.press(logged);
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/album/al1');
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull();
});

test('an empty feed says how to fill it', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], next_cursor: null }));
  render(<Feed />);
  expect(await screen.findByText(/Nothing yet. Log an album or post a photo/)).toBeTruthy();
});

test('stars are half steps', () => {
  expect(stars(10)).toBe('★ 5');
  expect(stars(7)).toBe('★ 3.5');
});
