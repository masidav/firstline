import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { ArtistLinks } from './ArtistLinks';
import { openTracked, spotifyArtistUrl } from './openLink';

const mockApi = { POST: jest.fn() };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: { api: mockApi } }) }));

const artist = {
  id: 'a1', name: 'Geese', spotify_artist_id: '2sSGPbdZJkaSE2AbcGOACx', status: 'active', tracked_at: '2026-10-01T00:00:00Z',
  website_url: 'https://geese.example.com', merch_url: 'https://shop.geese.example.com',
};

beforeEach(() => {
  jest.clearAllMocks();
  jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
});

test('a tap is counted and the link opens, even if counting fails', async () => {
  mockApi.POST.mockRejectedValueOnce(new Error('offline'));
  openTracked(mockApi as never, 'event_tickets', 'https://tm.example.com/e', { artistId: 'a1', eventId: 'e1' });
  expect(Linking.openURL).toHaveBeenCalledWith('https://tm.example.com/e');
  expect(mockApi.POST).toHaveBeenCalledWith('/clicks', { body: { kind: 'event_tickets', artist_id: 'a1', event_id: 'e1' } });
  await Promise.resolve();
});

test('the artist page shows merch, website, and Spotify, and counts each', async () => {
  mockApi.POST.mockResolvedValue({ response: { ok: true, status: 204 } });
  render(<ArtistLinks artist={artist as never} />);

  fireEvent.press(screen.getByRole('button', { name: 'Merch' }));
  expect(Linking.openURL).toHaveBeenLastCalledWith('https://shop.geese.example.com');
  expect(mockApi.POST).toHaveBeenLastCalledWith('/clicks', { body: { kind: 'artist_merch', artist_id: 'a1' } });
  fireEvent.press(screen.getByRole('button', { name: 'Spotify' }));
  await waitFor(() => expect(Linking.openURL).toHaveBeenLastCalledWith(spotifyArtistUrl(artist.spotify_artist_id)));
});

test('missing links are left out', () => {
  render(<ArtistLinks artist={{ ...artist, website_url: null, merch_url: null } as never} />);
  expect(screen.queryByRole('button', { name: 'Merch' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Website' })).toBeNull();
  expect(screen.getByRole('button', { name: 'Spotify' })).toBeTruthy();
});
