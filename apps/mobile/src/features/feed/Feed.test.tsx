import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import type { components } from '../../api/client';
import { DOOR_AFTER, Feed, stars, withDoor } from './Feed';
import { newSeed, statSentence, ticketsLine } from './feedText';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
const mockState = { status: 'signedIn', me: { handle: 'fan_one' } };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: mockState }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

const artist = { id: 'a1', name: 'Geese', image_url: null };
const base = { following: false, photo: null, log: null, grab_bag: null, concert: null, registration: null, stat: null };
const photo = {
  ...base,
  type: 'photo',
  id: 's1',
  at: '2026-10-21T12:00:00Z',
  user: { handle: 'fan_two', deleted: false, display_name: null },
  artist,
  photo: { kind: 'concert_photo', caption: 'Front row!', concert: { name: null, venue: 'Metro', city: 'Chicago', date: '2026-10-03' }, files: [{ id: 'f1', mime_type: 'image/jpeg', url: 'https://x/f1', redacted: false }] },
};
const log = {
  ...base,
  type: 'album_log',
  id: 'l1',
  at: '2026-10-21T11:00:00Z',
  user: { handle: 'fan_one', deleted: false, display_name: 'Ana' },
  artist,
  log: { album: { id: 'al1', title: '3D Country', cover_url: null }, rating: 9, review: 'Loud.', review_truncated: false, is_relisten: true, listened_on: '2026-10-20', visibility: 'followers' },
};
const song = {
  ...base,
  type: 'grab_bag',
  id: 'g1',
  at: '2026-10-21T10:00:00Z',
  user: { handle: 'fan_two', deleted: false, display_name: 'Ben' },
  artist,
  following: true,
  grab_bag: { id: 'g1', position: 2, spotify_track_id: '1aaaaaaaaaaaaaaaaaaaaa', title: 'Cowboy Nudes', artist, artist_status: 'active', album_title: '3D Country', cover_url: null, spotify_url: 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa', added_at: '2026-10-21T10:00:00Z' },
};
const event: components['schemas']['Event'] = { id: 'e1', artist_id: 'a1', name: 'Geese at Metro', venue: 'Metro', city_id: 'us-il-chicago', city: 'Chicago', region: 'IL', country: 'US', starts_at: '2026-11-20T02:00:00Z', tickets_available: true };
const allocation: components['schemas']['Allocation'] = { id: 'al-1', event_id: 'e1', name: 'Fan allocation', tickets: 100, face_value_cents: 4500, currency: 'USD', min_raw_score: 10, registration_opens_at: '2026-10-20T00:00:00Z', registration_closes_at: '2026-11-01T00:00:00Z', status: 'open', published_at: '2026-10-19T00:00:00Z', cancelled_at: null, selected_at: null, seats_left: null, delivery_hours_before_show: 24 };
const concert = { ...base, type: 'concert', id: 'e1', at: event.starts_at, user: null, artist, concert: { event, allocation, registered: false } };
const registration = {
  ...base,
  type: 'registration',
  id: 'r1',
  at: '2026-10-21T09:00:00Z',
  user: { handle: 'fan_one', deleted: false, display_name: 'Ana' },
  artist,
  registration: {
    registration: { id: 'r1', allocation_id: 'al-1', event_id: 'e1', hub_city_ids: ['us-il-chicago'], hub_match: 1, local: true, percentile: 70, raw_score: 70, is_estimated: false, registered_at: '2026-10-21T09:00:00Z', withdrawn_at: null, status: 'registered', plus_one: false, guest: null, selection_rank: null, seats: null },
    allocation,
    event,
    standing: { rank: 3, registrants: 40, top_percent: 8, seats_ahead: 2, seats_needed: 1, tickets: 100, margin_seats: 25, local: true, likely: true },
  },
};
const stat = {
  ...base,
  type: 'stat',
  id: 'st1',
  at: '2026-10-21T08:00:00Z',
  user: { handle: 'fan_two', deleted: false, display_name: 'Ben' },
  artist,
  following: true,
  stat: { kind: 'top_fan', value: 80, percentile: 97.4, rank: 2, fans: 40, is_estimated: false },
};

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.PUT.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
  mockApi.DELETE.mockResolvedValue({ data: undefined, response: { ok: true, status: 204 } });
});

test('shows every kind of card, keeps the seed while paging, and shuffles with a new seed', async () => {
  mockApi.GET.mockResolvedValueOnce(ok({ items: [photo, song, concert], next_cursor: 'c2', seed: 'abc' }))
    .mockResolvedValueOnce(ok({ items: [log, registration, stat], next_cursor: null, seed: 'abc' }))
    .mockResolvedValueOnce(ok({ items: [stat], next_cursor: null, seed: 'xyz' }));
  const openUrl = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
  render(<Feed />);

  expect(await screen.findByText('Front row!')).toBeTruthy();
  expect(screen.getByLabelText("@fan_two's a concert photo for Geese")).toBeTruthy();
  expect(screen.getByText('Metro, Chicago · Oct 3, 2026')).toBeTruthy();
  const firstSeed = (mockApi.GET.mock.calls[0][1] as { params: { query: { seed: string } } }).params.query.seed;
  expect(firstSeed).toMatch(/^[a-z0-9]+$/);

  // Grab-bag song: whose, which slot, and a Spotify link.
  expect(screen.getByText('Cowboy Nudes')).toBeTruthy();
  expect(screen.getByText('Geese · 3D Country')).toBeTruthy();
  fireEvent.press(screen.getByRole('link', { name: 'Open Cowboy Nudes on Spotify' }));
  expect(openUrl).toHaveBeenCalledWith('https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa');

  // Concert: opens the show.
  expect(screen.getByText(/Fan tickets: register by/)).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Open Geese at Metro' }));
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/show/e1');

  fireEvent.press(screen.getByRole('button', { name: 'Show more' }));
  const logged = await screen.findByRole('button', { name: 'Ana logged 3D Country by Geese' });
  expect(screen.getByText('Geese · ★ 4.5')).toBeTruthy();
  expect(screen.getByText(/followers only/)).toBeTruthy();
  expect(mockApi.GET).toHaveBeenLastCalledWith('/feed', { params: { query: { limit: 20, cursor: 'c2', seed: firstSeed } } });
  fireEvent.press(logged);
  expect(jest.requireMock('expo-router').router.push).toHaveBeenCalledWith('/album/al1');

  // Registration: the standing headline; stat: a sentence.
  expect(screen.getByText("You'll most likely get a ticket!")).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Open your registration for Geese at Metro' })).toBeTruthy();
  expect(screen.getByText('Ben is in the top 3% of Geese fans (#2 of 40 here)')).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Show more' })).toBeNull();

  // Shuffle asks again with a different seed.
  fireEvent.press(screen.getByRole('button', { name: 'Shuffle the feed' }));
  await waitFor(() => expect(mockApi.GET).toHaveBeenCalledTimes(3));
  const nextSeed = (mockApi.GET.mock.calls[2][1] as { params: { query: { seed: string; cursor?: string } } }).params.query;
  expect(nextSeed.seed).not.toBe(firstSeed);
  expect(nextSeed.cursor).toBeUndefined();
  await waitFor(() => expect(screen.queryByText('Front row!')).toBeNull());
});

test('Follow on a card follows that fan everywhere in the feed', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [photo, { ...song, following: false }, log], next_cursor: null, seed: 'abc' }));
  render(<Feed />);

  // fan_two's photo and song both say Follow; Ana's own log has no button.
  const follow = await screen.findAllByRole('button', { name: 'Follow @fan_two' });
  expect(follow).toHaveLength(2);
  expect(screen.queryByRole('button', { name: 'Follow @fan_one' })).toBeNull();

  fireEvent.press(follow[0]!);
  expect(mockApi.PUT).toHaveBeenCalledWith('/users/{handle}/follow', { params: { path: { handle: 'fan_two' } } });
  await waitFor(() => expect(screen.getAllByRole('button', { name: 'Unfollow @fan_two' })).toHaveLength(2));

  fireEvent.press(screen.getAllByRole('button', { name: 'Unfollow @fan_two' })[0]!);
  expect(mockApi.DELETE).toHaveBeenCalledWith('/users/{handle}/follow', { params: { path: { handle: 'fan_two' } } });
  await waitFor(() => expect(screen.getAllByRole('button', { name: 'Follow @fan_two' })).toHaveLength(2));
});

test('an empty feed says how to fill it', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [], next_cursor: null, seed: 'abc' }));
  render(<Feed />);
  expect(await screen.findByText(/Nothing yet. Follow some fans, log an album, or post a photo/)).toBeTruthy();
});

test('the header renders above the feed, and the feed hides while inactive', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [photo], next_cursor: null, seed: 'abc' }));
  const { rerender } = render(<Feed header={<>{null}</>} />);
  expect(await screen.findByText('Front row!')).toBeTruthy();
  expect(screen.getByRole('header', { name: 'Your feed' })).toBeTruthy();

  rerender(<Feed header={<>{null}</>} active={false} />);
  expect(screen.queryByText('Front row!')).toBeNull();
  expect(screen.queryByRole('header', { name: 'Your feed' })).toBeNull();
});

test('stars are half steps', () => {
  expect(stars(10)).toBe('★ 5');
  expect(stars(7)).toBe('★ 3.5');
});

test('stat sentences and ticket lines read naturally', () => {
  expect(statSentence('Ben', { kind: 'albums_logged', value: 1 }, null)).toBe('Ben has logged 1 album');
  expect(statSentence('Ben', { kind: 'concert_photos', value: 7 }, null)).toBe('Ben has 7 verified concert photos');
  expect(statSentence('Ben', { kind: 'merch', value: 2 }, null)).toBe('Ben has 2 pieces of verified merch');
  expect(statSentence('Ben', { kind: 'points', value: 310.5 }, null)).toBe('Ben has earned 311 points');
  expect(statSentence('Ben', { kind: 'top_fan', value: 50, percentile: 99.9, rank: 1, fans: 3, is_estimated: true }, 'Geese')).toBe(
    'Ben is in the top 1% of Geese fans (#1 of 3 here) · estimate',
  );
  expect(ticketsLine({ event, allocation: null, registered: false })).toBe('No fan tickets announced yet');
  expect(ticketsLine({ event, allocation, registered: true })).toBe("You're registered for fan tickets");
  expect(ticketsLine({ event, allocation: { ...allocation, status: 'upcoming' }, registered: false })).toMatch(/registration opens/);
  expect(ticketsLine({ event, allocation: { ...allocation, status: 'closed' }, registered: false })).toBe('Fan tickets: registration closed');
  expect(newSeed()).toMatch(/^[a-z0-9]{1,64}$/);
});

test("other fans' posts can be reported; your own can't", async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [photo, log], next_cursor: null, seed: 'abc' }));
  mockApi.POST.mockResolvedValue({ data: undefined, response: { ok: true, status: 202 } });
  render(<Feed />);

  // fan_one wrote the log (as "Ana"), so only fan_two's photo has a Report link.
  fireEvent.press(await screen.findByRole('button', { name: "Report @fan_two's post" }));
  expect(screen.queryByRole('button', { name: "Report Ana's post" })).toBeNull();

  expect(screen.getByRole('button', { name: 'Send report' })).toBeDisabled();
  fireEvent.press(screen.getByRole('radio', { name: 'Inappropriate' }));
  fireEvent.changeText(screen.getByLabelText('Details (optional)'), '  Not ok  ');
  fireEvent.press(screen.getByRole('button', { name: 'Send report' }));

  expect(await screen.findByText('Thanks. Staff will look at it.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/reports', {
    body: { target_type: 'photo', target_id: 's1', reason: 'inappropriate', details: 'Not ok' },
  });
});

test('the bouncer door sits after six items, or at the end of a shorter last page, and knocking draws a song', async () => {
  const many = Array.from({ length: 8 }, (_, i) => ({ ...log, id: `l${i}`, user: { handle: `fan_${i}`, deleted: false, display_name: `Fan ${i}` } }));
  const door = withDoor(many as never, false).map((row) => row.type);
  expect(door.indexOf('bouncer_door')).toBe(DOOR_AFTER);
  expect(withDoor(many.slice(0, 3) as never, true).some((row) => row.type === 'bouncer_door')).toBe(false);
  expect(withDoor(many.slice(0, 3) as never, false).map((row) => row.type).at(-1)).toBe('bouncer_door');
  expect(withDoor([], false)).toEqual([]);

  mockApi.GET.mockImplementation(async (path: string) =>
    path === '/feed'
      ? ok({ items: [photo], next_cursor: null, seed: 'abc' })
      : ok({ song: song.grab_bag, owner: { handle: 'fan_two', deleted: false, display_name: 'Ben' }, following: 1, songs: 2 }),
  );
  render(<Feed />);
  fireEvent.press(await screen.findByRole('button', { name: 'Knock on the bouncer door' }));
  expect(await screen.findByText('Ben let you in')).toBeTruthy();
  expect(screen.getByRole('link', { name: 'Open Cowboy Nudes on Spotify' })).toBeTruthy();
  expect(mockApi.GET).toHaveBeenCalledWith('/me/bouncer-door');
  expect(screen.getByRole('button', { name: 'Knock again on the bouncer door' })).toBeTruthy();
});
