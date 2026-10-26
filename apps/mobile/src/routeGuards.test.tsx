import { render } from '@testing-library/react-native';
import fs from 'node:fs';
import path from 'node:path';

// A reload on a deep link (an album, the journal) starts with the session still restoring from the stored refresh
// token. A route that redirects in that moment sends the fan to home and loses the page, so every route must wait.

let mockStatus = 'loading';
const mockRedirect = jest.fn();

jest.mock('./auth/SessionProvider', () => ({
  useSession: () => ({ session: {}, state: { status: mockStatus } }),
}));
jest.mock('expo-router', () => ({
  Redirect: (props: unknown) => {
    mockRedirect(props);
    return null;
  },
  router: { push: jest.fn(), replace: jest.fn(), back: jest.fn(), canGoBack: () => false },
  useLocalSearchParams: () => ({ albumId: 'a', artistId: 'a', eventId: 'e', handle: 'h', promptId: 'p' }),
}));

/** Every route file under src/app except the layout and the ones that are about being signed out. (This test lives
 * outside src/app because expo-router turns every file in it into a route.) */
function routeFiles(dir = path.join(__dirname, 'app')): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return routeFiles(full);
    return /\.tsx$/.test(entry.name) && !/\.test\.tsx$/.test(entry.name) ? [full] : [];
  });
}

const SKIPPED = ['_layout.tsx', 'index.tsx', 'sign-in.tsx', 'google-callback.tsx'];
const routes = routeFiles().filter((file) => !SKIPPED.includes(path.basename(file)));

beforeEach(() => {
  jest.clearAllMocks();
  mockStatus = 'loading';
});

test('there are routes to check', () => {
  expect(routes.length).toBeGreaterThan(20);
});

test.each(routes.map((file) => [path.relative(path.join(__dirname, 'app'), file), file]))('%s waits for the session instead of redirecting', (_name, file) => {
  const Route = (jest.requireActual(file as string) as { default: () => React.ReactElement | null }).default;

  render(<Route />);

  expect(mockRedirect).not.toHaveBeenCalled();
});

test.each(routes.map((file) => [path.relative(path.join(__dirname, 'app'), file), file]))('%s sends a signed-out fan to the start', (_name, file) => {
  mockStatus = 'signedOut';
  const Route = (jest.requireActual(file as string) as { default: () => React.ReactElement | null }).default;

  render(<Route />);

  expect(mockRedirect).toHaveBeenCalledWith(expect.objectContaining({ href: '/' }));
});
