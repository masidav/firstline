import { render, screen } from '@testing-library/react-native';

import { FanLevelSection, levelLabel, nextLevelLine } from './FanLevelSection';

const ok = (data: unknown) => ({ data, response: { ok: true, status: 200 } });
const mockApi = { GET: jest.fn() };
jest.mock('../../auth/SessionProvider', () => ({ useSession: () => ({ session: { api: mockApi } }) }));

test('the profile shows the level, how far along, and each part', async () => {
  mockApi.GET.mockResolvedValue(
    ok({
      level: 2,
      max_level: 5,
      activity: 0.3,
      next_level_at: 0.4,
      parts: [
        { key: 'fandom', value: 0.33 },
        { key: 'albums_logged', value: 0.18 },
        { key: 'verified_proof', value: 1 },
        { key: 'trivia', value: 0 },
        { key: 'prompts', value: 0 },
      ],
    }),
  );
  render(<FanLevelSection />);

  expect(await screen.findByText('Fan level 2 of 5')).toBeTruthy();
  expect(screen.getByText('30% now · next level at 40%')).toBeTruthy();
  expect(screen.getByLabelText('Verified proof: 100%')).toBeTruthy();
  expect(screen.getByLabelText('Trivia answered right: 0%')).toBeTruthy();
});

test('labels', () => {
  expect(levelLabel(5)).toBe('Fan level 5 of 5');
  expect(nextLevelLine({ activity: 0.9, next_level_at: null })).toBe("You're at the top level.");
});
