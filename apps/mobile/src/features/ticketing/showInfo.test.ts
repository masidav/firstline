import { saleLines, statusLabel, ticketLink, whenLabel } from './showInfo';

const base = {
  starts_at: '2026-12-05T01:00:00Z',
  time_tba: false,
  status: 'onsale' as const,
  presales: [] as { name: string; starts_at: string; ends_at: string | null }[],
  public_sale_starts_at: null as string | null,
  ticket_url: 'https://www.ticketmaster.com/event/ev-1',
};

test('status labels only for statuses worth showing', () => {
  expect(statusLabel('scheduled')).toBeNull();
  expect(statusLabel('cancelled')).toBe('Cancelled');
  expect(statusLabel('onsale')).toBe('On sale');
});

test('a show without a time reads its date as written, with TBA', () => {
  expect(whenLabel({ starts_at: '2027-01-15T06:00:00Z', time_tba: true })).toContain('time TBA');
  expect(whenLabel({ starts_at: '2027-01-15T06:00:00Z', time_tba: true })).toContain('15');
  expect(whenLabel(base)).not.toContain('TBA');
});

test('sales list presales then the public on-sale, and nothing for a cancelled show', () => {
  const now = new Date('2026-10-29T00:00:00Z');
  const show = {
    ...base,
    presales: [
      { name: 'Verified Fan Presale', starts_at: '2026-10-28T15:00:00Z', ends_at: '2026-10-30T03:00:00Z' },
      { name: 'Spotify Presale', starts_at: '2026-10-29T15:00:00Z', ends_at: null },
    ],
    public_sale_starts_at: '2026-11-01T15:00:00Z',
  };
  const lines = saleLines(show, now);
  expect(lines).toHaveLength(3);
  expect(lines[0]).toMatch(/^Verified Fan Presale: on now until /);
  expect(lines[1]).toMatch(/^Spotify Presale: /);
  expect(lines[2]).toMatch(/^Public on-sale: /);
  expect(saleLines({ ...show, status: 'cancelled' }, now)).toEqual([]);
  expect(saleLines({ ...show, public_sale_starts_at: '2026-10-01T00:00:00Z', presales: [] }, now)).toEqual([]);
});

test('no ticket link for a cancelled show', () => {
  expect(ticketLink(base)).toBe(base.ticket_url);
  expect(ticketLink({ ...base, status: 'cancelled' })).toBeNull();
  expect(ticketLink({ ...base, ticket_url: null })).toBeNull();
});
