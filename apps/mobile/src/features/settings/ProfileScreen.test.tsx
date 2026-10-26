import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { ProfileScreen, bandLinks } from './ProfileScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, errors: { field: string; message: string }[]) => ({
  error: { code: 'validation_failed', detail: 'Check the fields.', errors },
  response: { ok: false, status },
});

const mockApi = { PATCH: jest.fn(), POST: jest.fn() };
const mockSetMe = jest.fn();
const mockSession = { api: mockApi, setMe: mockSetMe, updateMe: jest.fn(), signOut: jest.fn(), deleteAccount: jest.fn() };

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession }),
}));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn() } }));
jest.mock('../authentication/appleSignIn', () => ({ reauthenticateWithApple: jest.fn() }));

const fan = {
  id: 'u-1',
  handle: 'fan',
  email: 'fan@example.test',
  created_at: '2026-10-24T12:00:00Z',
  hub_cities: [],
  hub_cities_change_available_at: null,
  is_artist: false,
  band_url: null,
  instagram_handle: null,
  x_handle: null,
  presale_reminders: true,
};
const artist = { ...fan, is_artist: true, band_url: 'https://open.spotify.com/artist/abc', instagram_handle: 'the.band', x_handle: 'theband' };

beforeEach(() => jest.clearAllMocks());

test('bandLinks builds the Instagram and X links and hides everything for non-artists', () => {
  expect(bandLinks(fan)).toEqual([]);
  expect(bandLinks({ ...artist, is_artist: false })).toEqual([]);
  expect(bandLinks(artist)).toEqual([
    { label: 'Band profile', url: 'https://open.spotify.com/artist/abc' },
    { label: 'Instagram @the.band', url: 'https://instagram.com/the.band' },
    { label: 'X @theband', url: 'https://x.com/theband' },
  ]);
});

test('a fan can become an artist and add the band links', async () => {
  const saved = { ...artist, instagram_handle: 'the.band' };
  mockApi.PATCH.mockResolvedValue(ok(saved));
  render(<ProfileScreen me={fan as never} />);

  expect(screen.queryByLabelText('Band profile link')).toBeNull();
  fireEvent(screen.getByLabelText("I'm an artist"), 'valueChange', true);
  fireEvent.changeText(screen.getByLabelText('Band profile link'), 'https://open.spotify.com/artist/abc');
  fireEvent.changeText(screen.getByLabelText('Instagram'), '@the.band');
  fireEvent.changeText(screen.getByLabelText('X'), 'theband');
  fireEvent.press(screen.getByText('Save'));

  await waitFor(() => expect(mockSetMe).toHaveBeenCalledWith(saved));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', {
    body: { is_artist: true, band_url: 'https://open.spotify.com/artist/abc', instagram_handle: 'the.band', x_handle: 'theband' },
  });
  expect(screen.getByText('Saved.')).toBeTruthy();
});

test('the band links show on the page and open in the browser', () => {
  const open = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
  render(<ProfileScreen me={artist as never} />);

  expect(screen.getByText('Your band')).toBeTruthy();
  fireEvent.press(screen.getByLabelText('Open Instagram @the.band'));
  expect(open).toHaveBeenCalledWith('https://instagram.com/the.band');
  fireEvent.press(screen.getByLabelText('Open Band profile'));
  expect(open).toHaveBeenCalledWith('https://open.spotify.com/artist/abc');
});

test('turning the artist switch off sends only is_artist and warns that links go', async () => {
  mockApi.PATCH.mockResolvedValue(ok({ ...fan }));
  render(<ProfileScreen me={artist as never} />);
  fireEvent(screen.getByLabelText("I'm an artist"), 'valueChange', false);
  expect(screen.getByText("Turning this off removes your band's links.")).toBeTruthy();
  fireEvent.press(screen.getByText('Save'));
  await waitFor(() => expect(mockSetMe).toHaveBeenCalledWith(fan));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { is_artist: false } });
});

test('field errors land on the band fields', async () => {
  mockApi.PATCH.mockResolvedValue(problem(422, [{ field: 'band_url', message: 'Use an https:// link.' }]));
  render(<ProfileScreen me={artist as never} />);
  fireEvent.changeText(screen.getByLabelText('Band profile link'), 'http://nope');
  fireEvent.press(screen.getByText('Save'));
  expect(await screen.findByText('Use an https:// link.')).toBeTruthy();
  expect(mockSetMe).not.toHaveBeenCalled();
});

test('a connected fan can resync Spotify; a second try within 15 minutes is explained', async () => {
  const spotify = { connected: true, scopes: [], connected_at: '2026-10-20T00:00:00Z', last_synced_at: '2026-10-25T10:00:00Z',
    sync_state: 'idle', sync_error: null, tracked_artists_found: 3, top_artists_added: 0 };
  mockApi.POST.mockResolvedValueOnce(ok({ ...spotify, sync_state: 'queued' }, 202))
    .mockResolvedValueOnce({ error: { code: 'rate_limited' }, response: { ok: false, status: 429 } });
  render(<ProfileScreen me={{ ...fan, spotify } as never} />);

  fireEvent.press(screen.getByRole('button', { name: 'Resync Spotify' }));
  await waitFor(() => expect(mockSession.updateMe).toHaveBeenCalledWith({ spotify: { ...spotify, sync_state: 'queued' } }));
  expect(mockApi.POST).toHaveBeenCalledWith('/me/spotify/sync');
  fireEvent.press(screen.getByRole('button', { name: 'Resync Spotify' }));
  expect(await screen.findByText(/synced in the last 15 minutes/)).toBeTruthy();
});

test('presale reminders switch off and on, saved at once', async () => {
  mockApi.PATCH.mockResolvedValueOnce(ok({ ...fan, presale_reminders: false }));
  render(<ProfileScreen me={fan} />);

  fireEvent(screen.getByLabelText('Presale reminders'), 'valueChange', false);
  await waitFor(() => expect(mockSession.updateMe).toHaveBeenCalledWith({ presale_reminders: false }));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { presale_reminders: false } });
});

test('deleting an Apple account first confirms with Apple and sends the fresh code', async () => {
  const { reauthenticateWithApple } = jest.requireMock('../authentication/appleSignIn');
  reauthenticateWithApple.mockResolvedValueOnce('cancelled').mockResolvedValueOnce({ code: 'fresh' });
  (mockSession as { provider?: string }).provider = 'apple';
  mockSession.deleteAccount.mockResolvedValue(undefined);
  render(<ProfileScreen me={fan} />);

  fireEvent.press(screen.getByRole('button', { name: 'Delete account' }));
  fireEvent.press(screen.getByRole('button', { name: 'Yes, delete my account' }));
  await waitFor(() => expect(reauthenticateWithApple).toHaveBeenCalledTimes(1));
  expect(mockSession.deleteAccount).not.toHaveBeenCalled();

  fireEvent.press(screen.getByRole('button', { name: 'Yes, delete my account' }));
  await waitFor(() => expect(mockSession.deleteAccount).toHaveBeenCalledWith('fresh'));
});

test('deleting a Google account does not involve Apple', async () => {
  const { reauthenticateWithApple } = jest.requireMock('../authentication/appleSignIn');
  (mockSession as { provider?: string }).provider = 'google';
  mockSession.deleteAccount.mockResolvedValue(undefined);
  render(<ProfileScreen me={fan} />);

  fireEvent.press(screen.getByRole('button', { name: 'Delete account' }));
  fireEvent.press(screen.getByRole('button', { name: 'Yes, delete my account' }));

  await waitFor(() => expect(mockSession.deleteAccount).toHaveBeenCalledWith(undefined));
  expect(reauthenticateWithApple).not.toHaveBeenCalled();
});
