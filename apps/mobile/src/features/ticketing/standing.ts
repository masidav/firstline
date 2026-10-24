import type { components } from '../../api/client';

export type Standing = components['schemas']['Standing'];

/**
 * What to tell a fan about their chances before and after registering. "Most likely" only when the API says
 * `likely`: everyone ahead plus the fan fit with a safety margin of seats to spare, so nobody gets false hope.
 */
export function standingMessage(standing: Standing): { headline: string; detail: string } {
  const place = `You're #${standing.rank} of ${standing.registrants} fans registered so far (top ${standing.top_percent}%).`;
  if (standing.likely) {
    return {
      headline: "You'll most likely get a ticket!",
      detail: `${place} Fans with higher scores can still register until registration closes, but there's room to spare.`,
    };
  }
  return {
    headline: `You're in the top ${standing.top_percent}% of fans registered so far.`,
    detail: standing.local
      ? `${place} Tickets go to the top fans when registration closes, so this isn't a sure thing yet.`
      : `${place} Fans who have this city as a hub get first pick, then everyone else by score.`,
  };
}

export function formatPrice(cents: number, currency: string): string {
  return `${(cents / 100).toFixed(2)} ${currency}`;
}
