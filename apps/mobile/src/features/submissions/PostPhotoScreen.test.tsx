import { fireEvent, render, screen } from '@testing-library/react-native';

import { PostPhotoScreen } from './PostPhotoScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const notFound = { error: { code: 'not_found' }, response: { ok: false, status: 404 } };
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { back: jest.fn(), replace: jest.fn() } }));
jest.mock('expo-image-manipulator', () => ({ manipulateAsync: jest.fn(), SaveFormat: { JPEG: 'jpeg' } }));
jest.mock('expo-image-picker', () => ({
  launchImageLibraryAsync: jest.fn().mockResolvedValue({ canceled: false, assets: [{ uri: 'file:///p.jpg', width: 800 }] }),
}));

function serve(events: unknown) {
  mockApi.GET.mockImplementation(async (path: string) => {
    if (path === '/me/scores') return ok({ items: [{ artist: { id: 'a1', name: 'Geese' } }], next_cursor: null });
    if (path === '/artists/{artistId}/events') return events;
    return ok({ items: [], next_cursor: null });
  });
}

beforeEach(() => jest.clearAllMocks());

test('a concert photo can only be sent once it is pinned to a show', async () => {
  serve(notFound);
  render(<PostPhotoScreen />);

  fireEvent.press(await screen.findByRole('radio', { name: 'Geese' }));
  fireEvent.press(screen.getByRole('button', { name: 'Choose a photo' }));
  await screen.findByLabelText('Chosen photo');

  // No listed shows: the fan types the concert in.
  expect(await screen.findByLabelText('Concert date')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Concert date'), '2020-10-03');
  fireEvent.changeText(screen.getByLabelText('Venue'), 'Metro');
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('City'), 'Chicago');
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeEnabled();
});

test('listed past shows are offered, and merch needs no concert', async () => {
  serve(ok({ items: [{ id: 'e1', name: 'Geese at Metro', venue: 'Metro', city: 'Chicago', starts_at: '2020-10-03T02:00:00Z' }], next_cursor: null }));
  render(<PostPhotoScreen />);

  fireEvent.press(await screen.findByRole('radio', { name: 'Geese' }));
  fireEvent.press(screen.getByRole('button', { name: 'Choose a photo' }));
  await screen.findByLabelText('Chosen photo');
  const show = await screen.findByRole('radio', { name: 'Metro, Chicago · Oct 3, 2020' });
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeDisabled();
  fireEvent.press(show);
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeEnabled();

  fireEvent.press(screen.getByRole('radio', { name: 'Merch' }));
  expect(screen.queryByText('Which concert?')).toBeNull();
  expect(screen.getByRole('button', { name: 'Send for review' })).toBeEnabled();
});
