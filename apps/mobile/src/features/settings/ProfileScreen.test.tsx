import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import Profile from '../../app/profile';
import { ProfileScreen, bandLinks } from './ProfileScreen';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const problem = (status: number, errors: { field: string; message: string }[]) => ({
  error: { code: 'validation_failed', detail: 'Check the fields.', errors },
  response: { ok: false, status },
});

const mockApi = { PATCH: jest.fn(), POST: jest.fn() };
const mockSetMe = jest.fn();
const mockSession = { api: mockApi, setMe: mockSetMe, updateMe: jest.fn(), signOut: jest.fn(), deleteAccount: jest.fn() };

// The session state, for the /profile route: it waits while the session restores and sends signed-out fans away.
let mockState: { status: string; me?: unknown } = { status: 'signedIn' };
const mockRedirect = jest.fn();

jest.mock('../../auth/SessionProvider', () => ({
  useSession: () => ({ session: mockSession, state: mockState }),
}));
jest.mock('../fan-level/FanLevelSection', () => ({ FanLevelSection: () => null }));
jest.mock('../notifications/push', () => ({
  pushSupported: () => false,
  registerForPush: jest.fn(),
  unregisterPush: jest.fn(() => Promise.resolve()),
}));
jest.mock('expo-router', () => ({
  router: { replace: jest.fn(), push: jest.fn() },
  Redirect: (props: unknown) => {
    mockRedirect(props);
    return null;
  },
}));
jest.mock('../authentication/appleSignIn', () => ({ reauthenticateWithApple: jest.fn() }));
jest.mock('../privacy/downloadMyData', () => ({ downloadMyData: jest.fn() }));

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
  push: { tickets: true, presales: true, staff: true },
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

test('Download my data saves the export, and says when the hourly limit is reached', async () => {
  const { downloadMyData } = jest.requireMock('../privacy/downloadMyData');
  const { ApiError } = jest.requireActual('../../api/client');
  downloadMyData
    .mockResolvedValueOnce('downloaded')
    .mockRejectedValueOnce(new ApiError(429, { code: 'rate_limited', title: 'Too Many Requests' }));
  render(<ProfileScreen me={fan} />);

  fireEvent.press(screen.getByRole('button', { name: 'Download my data' }));
  expect(await screen.findByText('Your data was downloaded as a JSON file.')).toBeTruthy();
  expect(downloadMyData).toHaveBeenCalledWith(mockApi);

  fireEvent.press(screen.getByRole('button', { name: 'Download my data' }));
  expect(await screen.findByText(/a few times in the last hour/)).toBeTruthy();
});

// Persistence: what the fan types is sent as a PATCH /me of only the changed fields, and the API's answer becomes
// the session's `me`, so every screen sees the saved profile.

test('Save is off until something changes; a new handle and display name are saved, and the saved state is shown', async () => {
  const saved = { ...fan, handle: 'ana', display_name: 'Ana' };
  mockApi.PATCH.mockResolvedValue(ok(saved));
  render(<ProfileScreen me={fan} />);

  expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  fireEvent.changeText(screen.getByLabelText('Handle'), ' ana ');
  fireEvent.changeText(screen.getByLabelText('Display name'), 'Ana');
  expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  expect(await screen.findByText('Saved.')).toBeTruthy();
  expect(mockApi.PATCH).toHaveBeenCalledTimes(1);
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { handle: 'ana', display_name: 'Ana' } });
  expect(mockSetMe).toHaveBeenCalledWith(saved);

  // The next edit clears "Saved." so the fan knows there is something new to save.
  fireEvent.changeText(screen.getByLabelText('Display name'), 'Ana B');
  expect(screen.queryByText('Saved.')).toBeNull();
});

test('a display name cleared to spaces is saved as none', async () => {
  mockApi.PATCH.mockResolvedValue(ok({ ...fan }));
  render(<ProfileScreen me={{ ...fan, display_name: 'Ana' }} />);

  fireEvent.changeText(screen.getByLabelText('Display name'), '   ');
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  await waitFor(() => expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { display_name: null } }));
});

test('a taken handle is explained on its field, and what was typed is kept', async () => {
  mockApi.PATCH.mockResolvedValue({ error: { code: 'handle_taken', title: 'Conflict' }, response: { ok: false, status: 409 } });
  render(<ProfileScreen me={fan} />);

  fireEvent.changeText(screen.getByLabelText('Handle'), 'ana');
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  expect(await screen.findByText('That handle is taken. Try another.')).toBeTruthy();
  expect(screen.getByLabelText('Handle').props.value).toBe('ana');
  expect(screen.queryByText('Saved.')).toBeNull();
  expect(mockSetMe).not.toHaveBeenCalled();
});

test('when the server cannot be reached, the form says so and nothing is marked saved', async () => {
  mockApi.PATCH.mockRejectedValue(new TypeError('Failed to fetch'));
  render(<ProfileScreen me={fan} />);

  fireEvent.changeText(screen.getByLabelText('Handle'), 'ana');
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  expect(await screen.findByText(/Can't reach the server/)).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  expect(mockSetMe).not.toHaveBeenCalled();
});

test('a failed presale-reminders switch is explained and leaves the setting alone', async () => {
  mockApi.PATCH.mockResolvedValue({ error: { code: 'internal_error', title: 'Server error' }, response: { ok: false, status: 500 } });
  render(<ProfileScreen me={fan} />);

  fireEvent(screen.getByLabelText('Presale reminders'), 'valueChange', false);

  expect(await screen.findByText('Server error')).toBeTruthy();
  expect(mockSession.updateMe).not.toHaveBeenCalled();
});

// Authorization: the page is for the signed-in fan only, staff-only entries show for staff only, and an ended
// session is explained rather than saved into.

test('the /profile route waits for the session, sends a signed-out fan to the start, and shows the signed-in fan', () => {
  mockState = { status: 'loading' };
  const { rerender } = render(<Profile />);
  expect(screen.getByLabelText('Loading')).toBeTruthy();
  expect(mockRedirect).not.toHaveBeenCalled();
  expect(screen.queryByText('Your profile')).toBeNull();

  mockState = { status: 'signedOut' };
  rerender(<Profile />);
  expect(mockRedirect).toHaveBeenCalledWith(expect.objectContaining({ href: '/' }));
  expect(screen.queryByText('Your profile')).toBeNull();

  mockState = { status: 'signedIn', me: fan };
  rerender(<Profile />);
  expect(screen.getByText('Your profile')).toBeTruthy();
  expect(screen.getByText('Signed in as fan@example.test')).toBeTruthy();
  mockState = { status: 'signedIn' };
});

test('staff-only entries show for staff and for nobody else', () => {
  const staffOnly = ['Review queue', 'Reported posts', 'Fraud review', 'Photo redaction', 'Artist links and sources', 'Featured artist', 'Weekly prompts'];
  const { rerender } = render(<ProfileScreen me={fan} />);
  for (const label of staffOnly) expect(screen.queryByRole('button', { name: label })).toBeNull();
  expect(screen.getByRole('button', { name: 'Stats' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Your public page' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Bouncer door' })).toBeNull();

  rerender(<ProfileScreen me={{ ...fan, is_staff: true }} />);
  for (const label of staffOnly) expect(screen.getByRole('button', { name: label })).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Review queue' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/review');
});

test('a session that has ended is explained instead of saved', async () => {
  mockApi.PATCH.mockResolvedValue({ error: { code: 'unauthorized', title: 'Unauthorized' }, response: { ok: false, status: 401 } });
  render(<ProfileScreen me={fan} />);

  fireEvent.changeText(screen.getByLabelText('Handle'), 'ana');
  fireEvent.press(screen.getByRole('button', { name: 'Save' }));

  expect(await screen.findByText('Your session has ended. Sign in again.')).toBeTruthy();
  expect(mockSetMe).not.toHaveBeenCalled();
});

test('Sign out forgets this device for push before ending the session', async () => {
  const { unregisterPush } = jest.requireMock('../notifications/push');
  render(<ProfileScreen me={fan} />);

  fireEvent.press(screen.getByRole('button', { name: 'Sign out' }));

  await waitFor(() => expect(mockSession.signOut).toHaveBeenCalledTimes(1));
  expect(unregisterPush).toHaveBeenCalledWith(mockApi);
  expect(unregisterPush.mock.invocationCallOrder[0]).toBeLessThan(mockSession.signOut.mock.invocationCallOrder[0]!);
});
