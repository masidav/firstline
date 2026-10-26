import type { components } from '../../api/client';

type Show = components['schemas']['Event'];

const STATUS_LABELS: Partial<Record<Show['status'], string>> = {
  onsale: 'On sale',
  offsale: 'Off sale',
  cancelled: 'Cancelled',
  postponed: 'Postponed',
  rescheduled: 'Rescheduled',
};

/** A short label for the show's Ticketmaster status, or null for a plain scheduled show. */
export function statusLabel(status: Show['status']): string | null {
  return STATUS_LABELS[status] ?? null;
}

const day = (iso: string) =>
  new Date(iso).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });
const dayTime = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** When the show is: the date and time, or just the date when Ticketmaster hasn't announced the time. */
export function whenLabel(show: Pick<Show, 'starts_at' | 'time_tba'>): string {
  if (show.time_tba) {
    // starts_at is local midnight at the venue; read the date part as written, not shifted to this device.
    const [y, m, d] = show.starts_at.slice(0, 10).split('-').map(Number);
    const date = new Date(y!, m! - 1, d!).toLocaleDateString(undefined, {
      weekday: 'short',
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
    return `${date} · time TBA`;
  }
  return `${day(show.starts_at)}, ${new Date(show.starts_at).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`;
}

/** Upcoming sales, soonest first: each presale, then the public on-sale if it hasn't started. */
export function saleLines(show: Pick<Show, 'presales' | 'public_sale_starts_at' | 'status'>, now: Date = new Date()): string[] {
  if (show.status === 'cancelled') return [];
  const lines = show.presales.map((p) =>
    new Date(p.starts_at) <= now
      ? `${p.name}: on now${p.ends_at ? ` until ${dayTime(p.ends_at)}` : ''}`
      : `${p.name}: ${dayTime(p.starts_at)}`,
  );
  if (show.public_sale_starts_at && new Date(show.public_sale_starts_at) > now) {
    lines.push(`Public on-sale: ${dayTime(show.public_sale_starts_at)}`);
  }
  return lines;
}

/** The ticket link to offer, if the show still sells tickets. */
export function ticketLink(show: Pick<Show, 'ticket_url' | 'status'>): string | null {
  return show.ticket_url && show.status !== 'cancelled' ? show.ticket_url : null;
}
