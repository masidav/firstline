import type { components } from '../../api/client';

export type FeedItem = components['schemas']['FeedItem'];
export type FeedShow = components['schemas']['FeedShow'];
export type FeedStat = components['schemas']['FeedStat'];

/** Ratings are half stars: 9 → "★ 4.5". */
export function stars(rating: number): string {
  return `★ ${(rating / 2).toFixed(rating % 2 === 0 ? 0 : 1)}`;
}

/** "Oct 3, 2026, 8:00 PM" for a show's start, in the fan's time zone. */
export function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });
}

/** A followed fan's number as one sentence. `who` is their display name or @handle. */
export function statSentence(who: string, stat: FeedStat, artistName: string | null | undefined): string {
  const n = Math.round(stat.value);
  switch (stat.kind) {
    case 'top_fan': {
      const top = Math.max(1, Math.round(100 - (stat.percentile ?? 0)));
      const place = stat.rank != null && stat.fans != null ? ` (#${stat.rank} of ${stat.fans} here)` : '';
      const estimate = stat.is_estimated ? ' · estimate' : '';
      return `${who} is in the top ${top}% of ${artistName ?? 'this artist'} fans${place}${estimate}`;
    }
    case 'albums_logged':
      return `${who} has logged ${n} album${n === 1 ? '' : 's'}`;
    case 'concert_photos':
      return `${who} has ${n} verified concert photo${n === 1 ? '' : 's'}`;
    case 'merch':
      return `${who} has ${n} piece${n === 1 ? '' : 's'} of verified merch`;
    case 'points':
      return `${who} has earned ${n} points`;
    default:
      return `${who}: ${n}`;
  }
}

/** What the show's fan allocation means for this fan right now. */
export function ticketsLine(show: FeedShow): string {
  if (show.registered) return "You're registered for fan tickets";
  const allocation = show.allocation;
  if (!allocation) return 'No fan tickets announced yet';
  if (allocation.status === 'open') return `Fan tickets: register by ${formatWhen(allocation.registration_closes_at)}`;
  if (allocation.status === 'upcoming') return `Fan tickets: registration opens ${formatWhen(allocation.registration_opens_at)}`;
  return 'Fan tickets: registration closed';
}

/** A short random key for the feed's shuffle; letters and digits only (the API's `seed` pattern). */
export function newSeed(): string {
  return Math.random().toString(36).slice(2, 12) || 'seed';
}
