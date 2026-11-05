import { fireEvent, render, screen, waitFor, within } from '@testing-library/react-native';
import { Image } from 'react-native';

import { deletesIn, RedactionScreen } from './RedactionScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('expo-router', () => ({ router: { replace: jest.fn(), push: jest.fn() } }));

const item = (
  id: string,
  kind: string,
  note: string,
  codes = 0,
  user: Record<string, unknown> = { handle: 'fan', deleted: false, avatar_url: 'https://api.test/v1/avatars/1' },
) => ({
  file_id: id,
  submission_id: `s-${id}`,
  kind,
  artist: { id: 'a1', name: 'Geese', image_url: null },
  user,
  note,
  codes_redacted: codes,
  uploaded_at: '2026-10-20T12:00:00Z',
  deleted_after: '2099-11-19T12:00:00Z',
  file: { id, mime_type: 'image/jpeg', url: `https://api.example.test/v1/files/${id}`, redacted: false },
});

const touch = (x: number, y: number) => ({ nativeEvent: { locationX: x, locationY: y } });

beforeEach(() => {
  jest.clearAllMocks();
  // A 1400×1050 photo: the canvas takes its 4:3 ratio, so a 400×300 layout shows it edge to edge.
  jest.spyOn(Image, 'getSize').mockImplementation((_uri, success) => success(1400, 1050));
});

test('staff archive a checked photo or discard it', async () => {
  mockApi.GET.mockResolvedValue(
    ok({
      items: [
        item('f1', 'ticket', 'No barcode or QR code was found on this ticket. Check it by eye.'),
        item('f2', 'screenshot', 'Codes were still found after 3 rounds of redacting.', 2, { handle: null, deleted: true, avatar_url: null }),
      ],
    }),
  );
  mockApi.POST.mockResolvedValue(ok(undefined));
  render(<RedactionScreen />);

  expect(await screen.findByText('No barcode or QR code was found on this ticket. Check it by eye.')).toBeTruthy();
  expect(screen.getByText(/2 codes already blacked out/)).toBeTruthy();
  // The fan's picture sits beside their handle; a deleted fan gets the "?" placeholder.
  expect(screen.getByLabelText("@fan's picture").props.source).toEqual({ uri: 'https://api.test/v1/avatars/1' });
  expect(screen.getByText('Screenshot · Geese · Deleted user')).toBeTruthy();
  expect(within(screen.getByLabelText("Deleted user's picture")).getByText('?')).toBeTruthy();

  fireEvent.press(screen.getAllByRole('button', { name: 'Archive' })[0]!);
  await waitFor(() => expect(screen.queryByText(/found on this ticket/)).toBeNull());
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/redactions/{fileId}/decision', {
    params: { path: { fileId: 'f1' } },
    body: { action: 'archive' },
  });

  fireEvent.press(screen.getByRole('button', { name: 'Discard photo' }));
  expect(await screen.findByText('Nothing waiting.')).toBeTruthy();
  expect(mockApi.POST).toHaveBeenLastCalledWith('/admin/redactions/{fileId}/decision', {
    params: { path: { fileId: 'f2' } },
    body: { action: 'discard' },
  });
});

test('staff draw boxes over what the scanner missed and archive with them', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [item('f1', 'ticket', 'No barcode or QR code was found on this ticket. Check it by eye.')] }));
  mockApi.POST.mockResolvedValue(ok(undefined));
  render(<RedactionScreen />);
  const photo = await screen.findByTestId('photo-f1');

  // Not in drawing mode: the photo doesn't take the touch (the page scrolls instead).
  expect(photo.props.onStartShouldSetResponder()).toBe(false);

  fireEvent.press(screen.getByRole('button', { name: 'Draw boxes' }));
  expect(photo.props.onStartShouldSetResponder()).toBe(true);
  expect(screen.getByText(/0 boxes so far/)).toBeTruthy();
  // (Fired here because the test library drops events on a responder that isn't accepting touches.)
  fireEvent(photo, 'layout', { nativeEvent: { layout: { x: 0, y: 0, width: 400, height: 300 } } });

  // An order number in the top-right corner, dragged from its bottom-right to its top-left.
  fireEvent(photo, 'responderGrant', touch(380, 60));
  fireEvent(photo, 'responderMove', touch(300, 40));
  fireEvent(photo, 'responderRelease', touch(260, 30));
  expect(screen.getByText(/1 box so far/)).toBeTruthy();
  expect(screen.getByTestId('box-f1-0').props.style).toEqual(
    expect.arrayContaining([expect.objectContaining({ left: 260, top: 30, width: 120, height: 30 })]),
  );

  // A tap adds nothing; a second box does.
  fireEvent(photo, 'responderGrant', touch(100, 100));
  fireEvent(photo, 'responderRelease', touch(101, 102));
  expect(screen.getByText(/1 box so far/)).toBeTruthy();
  fireEvent(photo, 'responderGrant', touch(0, 270));
  fireEvent(photo, 'responderRelease', touch(400, 300));
  expect(screen.getByText(/2 boxes so far/)).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Remove last box' }));
  expect(screen.getByText(/1 box so far/)).toBeTruthy();
  fireEvent(photo, 'responderGrant', touch(0, 270));
  fireEvent(photo, 'responderRelease', touch(400, 300));

  fireEvent.press(screen.getByRole('button', { name: 'Done drawing' }));
  fireEvent.press(screen.getByRole('button', { name: 'Archive with 2 boxes blacked out' }));
  await waitFor(() => expect(screen.getByText('Nothing waiting.')).toBeTruthy());
  expect(mockApi.POST).toHaveBeenCalledWith('/admin/redactions/{fileId}/decision', {
    params: { path: { fileId: 'f1' } },
    body: {
      action: 'archive',
      boxes: [
        { x: 0.65, y: 0.1, w: 0.3, h: 0.1 },
        { x: 0, y: 0.9, w: 1, h: 0.1 },
      ],
    },
  });
});

test('says when the original is deleted', () => {
  const now = new Date('2026-10-26T12:00:00Z');
  expect(deletesIn('2026-10-29T13:00:00Z', now)).toBe('in 3 days');
  expect(deletesIn('2026-10-27T13:00:00Z', now)).toBe('in 1 day');
  expect(deletesIn('2026-10-26T20:00:00Z', now)).toBe('today');
  expect(deletesIn('2026-10-25T12:00:00Z', now)).toBe('overdue');
});
