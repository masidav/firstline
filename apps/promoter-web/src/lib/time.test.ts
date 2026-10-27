import { formatInZone, isoToZoned, zonedToIso } from './time';

test('a wall-clock time in the show city becomes the right instant, summer and winter', () => {
  expect(zonedToIso('2026-11-27T20:00', 'America/Chicago')).toBe('2026-11-28T02:00:00.000Z'); // CST, UTC-6
  expect(zonedToIso('2026-07-04T20:00', 'America/Chicago')).toBe('2026-07-05T01:00:00.000Z'); // CDT, UTC-5
  expect(zonedToIso('2026-11-27T20:00', 'America/Los_Angeles')).toBe('2026-11-28T04:00:00.000Z');
  expect(zonedToIso('2026-07-04T20:00', 'America/Phoenix')).toBe('2026-07-05T03:00:00.000Z'); // no daylight saving
});

test('times right after a daylight-saving change land on the right side of it', () => {
  // US clocks go back at 2:00 on 2026-11-01 and forward at 2:00 on 2026-03-08.
  expect(zonedToIso('2026-11-01T03:00', 'America/New_York')).toBe('2026-11-01T08:00:00.000Z');
  expect(zonedToIso('2026-03-08T03:00', 'America/New_York')).toBe('2026-03-08T07:00:00.000Z');
});

test('an instant reads back as the same wall clock, and is shown with its zone', () => {
  expect(isoToZoned('2026-11-28T02:00:00.000Z', 'America/Chicago')).toBe('2026-11-27T20:00');
  expect(formatInZone('2026-11-28T02:00:00.000Z', 'America/Chicago')).toMatch(/Nov 27, 2026.*8:00\s?PM CST/);
});
