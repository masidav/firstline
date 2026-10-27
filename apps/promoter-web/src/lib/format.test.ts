import { can, formatMoney, toCsv } from './format';

test('CSV quotes what needs quoting and leaves empty cells empty', () => {
  expect(toCsv([['handle', 'note'], ['ana', 'said "hi", then left'], ['bo', null]])).toBe(
    'handle,note\r\nana,"said ""hi"", then left"\r\nbo,\r\n',
  );
});

test('money and roles', () => {
  expect(formatMoney(4500, 'USD')).toBe('$45.00');
  expect([can.manage('owner'), can.manage('admin'), can.manage('analyst')]).toEqual([true, true, false]);
  expect([can.manageMembers('owner'), can.manageMembers('admin')]).toEqual([true, false]);
});
