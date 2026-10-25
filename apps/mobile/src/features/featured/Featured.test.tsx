import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Linking } from 'react-native';

import { FeaturedAdminScreen, mondayOf, uploadVideo } from './FeaturedAdminScreen';
import { FeaturedArtistCard } from './FeaturedArtistCard';

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });
const mockApi = { GET: jest.fn(), POST: jest.fn(), PUT: jest.fn(), DELETE: jest.fn() };
const mockSession = { api: mockApi };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'staff', is_staff: true } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));
jest.mock('expo-image-picker', () => ({ launchImageLibraryAsync: jest.fn() }));

const pick = {
  id: 'f1', week_start: '2026-10-19', week_end: '2026-10-25', artist: { id: 'a1', name: 'Geese', image_url: null }, headline: 'On tour now',
  video: { kind: 'youtube', url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ', embed_url: 'https://www.youtube.com/embed/dQw4w9WgXcQ',
           thumbnail_url: 'https://img.youtube.com/vi/dQw4w9WgXcQ/hqdefault.jpg' },
  song: { spotify_track_id: '1aaaaaaaaaaaaaaaaaaaaa', title: 'Trinidad', album_title: 'Getting Killed', cover_url: null,
          spotify_url: 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa' },
};

beforeEach(() => jest.clearAllMocks());

test('the featured card shows the artist, opens the video and the song', async () => {
  mockApi.GET.mockResolvedValue(ok({ featured: pick }));
  const openUrl = jest.spyOn(Linking, 'openURL').mockResolvedValue(true);
  render(<FeaturedArtistCard />);

  expect(await screen.findByText('Geese')).toBeTruthy();
  expect(screen.getByText('On tour now')).toBeTruthy();
  fireEvent.press(screen.getByRole('link', { name: 'Watch on YouTube: Geese' }));
  expect(openUrl).toHaveBeenCalledWith('https://www.youtube.com/watch?v=dQw4w9WgXcQ');
  fireEvent.press(screen.getByRole('link', { name: 'Listen to Trinidad on Spotify' }));
  expect(openUrl).toHaveBeenCalledWith('https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa');
});

test('staff pick an artist, a video link, and a song for a week', async () => {
  mockApi.GET.mockImplementation(async (path: string) =>
    path === '/admin/featured' ? ok({ items: [] }) : ok({ items: [{ id: 'a1', name: 'Geese', status: 'active' }], next_cursor: null }));
  mockApi.PUT.mockResolvedValue(ok({ ...pick, week_start: mondayOf(1) }));
  render(<FeaturedAdminScreen />);

  expect(await screen.findByText('No picks yet.')).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Save pick' })).toBeDisabled();
  fireEvent.press(screen.getByRole('button', { name: 'Next week' }));
  const artist = screen.getByLabelText('Artist');
  fireEvent.changeText(artist, 'gee');
  fireEvent(artist, 'submitEditing');
  fireEvent.press(await screen.findByRole('button', { name: 'Pick Geese' }));
  fireEvent.changeText(screen.getByLabelText('Video link (YouTube or TikTok)'), 'https://youtu.be/dQw4w9WgXcQ');
  fireEvent.changeText(screen.getByLabelText('Spotify song link'), 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa');
  fireEvent.press(screen.getByRole('button', { name: 'Save pick' }));

  expect(await screen.findByText(`Saved: Geese for the week of ${mondayOf(1)}.`)).toBeTruthy();
  expect(mockApi.PUT).toHaveBeenCalledWith('/admin/featured/{weekStart}', {
    params: { path: { weekStart: mondayOf(1) } },
    body: { artist_id: 'a1', headline: null, video_url: 'https://youtu.be/dQw4w9WgXcQ', spotify_track: 'https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa' },
  });
});

test('mondays and video uploads', async () => {
  expect(mondayOf(0, new Date('2026-10-25T12:00:00Z'))).toBe('2026-10-19');   // a Sunday belongs to the week before
  expect(mondayOf(1, new Date('2026-10-19T00:00:00Z'))).toBe('2026-10-26');
  const blob = { size: 20 };
  const fetchImpl = jest.fn().mockResolvedValueOnce({ blob: async () => blob }).mockResolvedValueOnce({ ok: true, status: 204 });
  mockApi.POST.mockResolvedValue(ok({ upload_id: 'u1', method: 'PUT', url: 'https://api/upload', headers: { 'Content-Type': 'video/mp4' } }, 201));
  await expect(uploadVideo(mockApi as never, 'file:///clip.mp4', fetchImpl as never)).resolves.toBe('u1');
  expect(mockApi.POST).toHaveBeenCalledWith('/uploads', { body: { purpose: 'featured_video', mime_type: 'video/mp4', bytes: 20 } });
  await waitFor(() => expect(fetchImpl).toHaveBeenLastCalledWith('https://api/upload', { method: 'PUT', headers: { 'Content-Type': 'video/mp4' }, body: blob }));
});
