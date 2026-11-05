import { render } from '@testing-library/react-native';

import RootLayout from './app/_layout';

const mockMotion = { reduced: false };
const mockStack = jest.fn((_props: unknown) => null);

jest.mock('expo-router', () => ({ Stack: (props: unknown) => mockStack(props), useSegments: () => [] }));
jest.mock('expo-status-bar', () => ({ StatusBar: () => null }));
jest.mock('react-native-safe-area-context', () => ({ SafeAreaProvider: ({ children }: { children: unknown }) => children }));
jest.mock('./auth/SessionProvider', () => ({ SessionProvider: ({ children }: { children: unknown }) => children }));
jest.mock('./features/album-journal/JournalSync', () => ({ JournalSync: () => null }));
jest.mock('./features/notifications/PushSetup', () => ({ PushSetup: () => null }));
jest.mock('./features/spotify/Library', () => ({ SpotifyLibraryProvider: ({ children }: { children: unknown }) => children }));
jest.mock('./features/spotify/NowPlaying', () => ({ NowPlayingProvider: ({ children }: { children: unknown }) => children, NowPlayingBar: () => null }));
jest.mock('./ui/useReducedMotion', () => ({ useReducedMotion: () => mockMotion.reduced }));

beforeEach(() => jest.clearAllMocks());

function stackAnimation(): string {
  return (mockStack.mock.calls[0] as unknown as [{ screenOptions: { animation: string } }])[0].screenOptions.animation;
}

test('screens slide in by default and cross-fade with Reduce Motion on', () => {
  mockMotion.reduced = false;
  render(<RootLayout />);
  expect(stackAnimation()).toBe('default');

  jest.clearAllMocks();
  mockMotion.reduced = true;
  render(<RootLayout />);
  expect(stackAnimation()).toBe('fade');
  mockMotion.reduced = false;
});
