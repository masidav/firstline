import { fireEvent, render, screen } from '@testing-library/react-native';

import { MyTicketsScreen, reservationLine } from './MyTicketsScreen';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn(), POST: jest.fn() };
const mockSession = { api: mockApi };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession, state: { status: 'signedIn', me: { handle: 'ana' } } }) }));
jest.mock('expo-router', () => ({ router: { push: jest.fn(), back: jest.fn(), replace: jest.fn(), canGoBack: () => false } }));

const event = { id: 'e1', artist_id: 'a1', name: 'Geese at Metro', venue: 'Metro', city_id: 'us-il-chicago', city: 'Chicago', region: 'IL', country: 'US', starts_at: '2026-11-20T02:00:00Z', tickets_available: false, source: null, ticket_url: null, status: 'scheduled', time_tba: false, public_sale_starts_at: null, public_sale_ends_at: null, presales: [] };
const base = {
  id: 'res-1', allocation_id: 'al-1', event_id: 'e1', registration_id: 'r1', status: 'held', role: 'holder', seats: 2,
  face_value_cents: 4500, currency: 'USD', deliver_at: '2026-11-18T02:00:00Z', created_at: '2026-10-25T00:00:00Z',
  returned_at: null, cancelled_at: null, cancelled_reason: null, holder_handle: 'ana', guest_handle: 'gus', can_return: true,
  ticket: null, event, artist: { id: 'a1', name: 'Geese', image_url: null }, allocation_name: 'Fan allocation',
};
const token = 'a'.repeat(32) + 'b'.repeat(32);

beforeEach(() => jest.clearAllMocks());

test('a held reservation says when the ticket arrives, and can be returned after confirming', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [base] }));
  mockApi.POST.mockResolvedValue(ok({ ...base, status: 'returned', returned_at: '2026-10-26T00:00:00Z', can_return: false }));
  render(<MyTicketsScreen />);

  expect(await screen.findByText('Geese at Metro')).toBeTruthy();
  expect(screen.getByText(/Your seats are held\. The ticket arrives/)).toBeTruthy();
  expect(screen.getByText('Your +1: @gus')).toBeTruthy();
  expect(screen.getByText('2 seats · 45.00 USD each')).toBeTruthy();

  fireEvent.press(screen.getByRole('button', { name: 'Return your ticket for Geese at Metro' }));
  expect(screen.getByText(/The next fan in line gets them at face value/)).toBeTruthy();
  fireEvent.press(screen.getByRole('button', { name: 'Keep it' }));
  expect(mockApi.POST).not.toHaveBeenCalled();

  fireEvent.press(screen.getByRole('button', { name: 'Return your ticket for Geese at Metro' }));
  fireEvent.press(screen.getByRole('button', { name: 'Yes, return it' }));
  expect(await screen.findByText(/The seats went to the next fan in line/)).toBeTruthy();
  expect(mockApi.POST).toHaveBeenCalledWith('/reservations/{reservationId}/return', { params: { path: { reservationId: 'res-1' } } });
  expect(screen.queryByRole('button', { name: /Return your ticket/ })).toBeNull();
});

test('a delivered ticket shows the token to the holder; a guest sees the reservation without it', async () => {
  const delivered = { ...base, status: 'delivered', ticket: { token, holder_handle: 'ana', guest_handle: 'gus' } };
  const asGuest = { ...base, id: 'res-2', status: 'delivered', role: 'guest', holder_handle: 'ben', guest_handle: 'ana', can_return: false, ticket: null };
  mockApi.GET.mockResolvedValue(ok({ items: [delivered, asGuest] }));
  render(<MyTicketsScreen />);

  expect(await screen.findByText('Your ticket is here. Show it at the door.')).toBeTruthy();
  expect(screen.getByText('Ticket · @ana + @gus')).toBeTruthy();
  expect(screen.getByText(token.replace(/(.{8})/g, '$1 ').trim())).toBeTruthy();
  expect(screen.getByText("You're @ben's +1. They show the ticket at the door.")).toBeTruthy();
  expect(screen.getAllByRole('button', { name: /Return your ticket/ })).toHaveLength(1);
});

test('an empty list says how to get a ticket, and lines read well', async () => {
  mockApi.GET.mockResolvedValue(ok({ items: [] }));
  render(<MyTicketsScreen />);
  expect(await screen.findByText(/No tickets yet/)).toBeTruthy();
  expect(reservationLine({ ...base, status: 'cancelled', cancelled_reason: 'account_deleted' } as never)).toBe('This reservation was cancelled.');
  expect(reservationLine({ ...base, seats: 1 } as never)).toMatch(/^Your seat is held/);
});
