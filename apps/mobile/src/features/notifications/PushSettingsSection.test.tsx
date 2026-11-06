import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';

import { PushSettingsSection } from './PushSettingsSection';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { PATCH: jest.fn() };
const mockSession = { api: mockApi, updateMe: jest.fn() };

jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: mockSession }) }));
jest.mock('./push', () => ({ pushSupported: jest.fn(() => true), registerForPush: jest.fn() }));
const mockPush = jest.requireMock('./push') as { pushSupported: jest.Mock; registerForPush: jest.Mock };

const me = {
  id: 'u-1', handle: 'fan', email: 'fan@example.test', created_at: '2026-10-24T12:00:00Z', hub_cities: [], is_artist: false,
  presale_reminders: true, is_staff: false, push: { tickets: true, presales: true, staff: true }, email_settings: { staff: true },
};
const staff = { ...me, handle: 'staffer', email: 'staffer@example.test', is_staff: true };

beforeEach(() => {
  jest.clearAllMocks();
  mockPush.pushSupported.mockReturnValue(true);
});

test('turning push on asks the phone, then shows the categories', async () => {
  mockPush.registerForPush.mockResolvedValueOnce('off').mockResolvedValueOnce('registered');
  render(<PushSettingsSection me={me as never} />);

  fireEvent.press(await screen.findByRole('button', { name: 'Turn on push notifications' }));
  expect(await screen.findByLabelText('Push: Presale reminders')).toBeTruthy();
  expect(mockPush.registerForPush).toHaveBeenLastCalledWith(mockApi, true);
  expect(screen.queryByLabelText('Push: Staff notices')).toBeNull();   // not staff
  expect(screen.queryByLabelText('Email: Staff notices')).toBeNull();
});

test('a category switch saves at once', async () => {
  mockPush.registerForPush.mockResolvedValue('registered');
  mockApi.PATCH.mockResolvedValue(ok({ ...me, push: { ...me.push, presales: false } }));
  render(<PushSettingsSection me={me as never} />);

  fireEvent(await screen.findByLabelText('Push: Presale reminders'), 'valueChange', false);
  await waitFor(() => expect(mockSession.updateMe).toHaveBeenCalledWith({ push: { tickets: true, presales: false, staff: true } }));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { push: { presales: false } } });
});

test('on the web it explains push is for the phone app', () => {
  mockPush.pushSupported.mockReturnValue(false);
  mockPush.registerForPush.mockResolvedValue('unsupported');
  render(<PushSettingsSection me={me as never} />);
  expect(screen.getByText(/work in the phone app/)).toBeTruthy();
  expect(screen.queryByLabelText('Email: Staff notices')).toBeNull();
});

test('a refusal points to the phone settings', async () => {
  mockPush.registerForPush.mockResolvedValue('denied');
  render(<PushSettingsSection me={me as never} />);
  expect(await screen.findByRole('button', { name: 'Open settings' })).toBeTruthy();
});

test('staff choose email notices on the web too, and the switch saves at once', async () => {
  mockPush.pushSupported.mockReturnValue(false);
  mockApi.PATCH.mockResolvedValue(ok({ ...staff, email_settings: { staff: false } }));
  render(<PushSettingsSection me={staff as never} />);

  expect(screen.getByText('Staff notices by email, to staffer@example.test')).toBeTruthy();
  fireEvent(screen.getByLabelText('Email: Staff notices'), 'valueChange', false);
  await waitFor(() => expect(mockSession.updateMe).toHaveBeenCalledWith({ email_settings: { staff: false } }));
  expect(mockApi.PATCH).toHaveBeenCalledWith('/me', { body: { email_settings: { staff: false } } });
});

test('on the phone the email switch sits under the push categories', async () => {
  mockPush.registerForPush.mockResolvedValue('registered');
  render(<PushSettingsSection me={staff as never} />);
  expect(await screen.findByLabelText('Push: Staff notices')).toBeTruthy();
  expect(screen.getByLabelText('Email: Staff notices')).toBeTruthy();
});
