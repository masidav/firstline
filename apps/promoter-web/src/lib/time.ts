// Shows happen in their city's time zone (HubCity.time_zone), whoever is looking. Times are entered and shown in that
// zone and sent to the API as UTC instants.

function parts(instant: number, timeZone: string): Record<string, number> {
  const formatted = new Intl.DateTimeFormat('en-US', {
    timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(new Date(instant));
  return Object.fromEntries(formatted.filter((p) => p.type !== 'literal').map((p) => [p.type, Number(p.value)]));
}

/** How far the zone's wall clock is ahead of UTC at this instant, in milliseconds. */
function offset(instant: number, timeZone: string): number {
  const p = parts(instant, timeZone);
  return Date.UTC(p.year!, p.month! - 1, p.day!, p.hour!, p.minute!, p.second!) - Math.floor(instant / 1000) * 1000;
}

/**
 * A wall-clock time ("2026-11-27T20:00", as a datetime-local input gives it) in a time zone, as an ISO instant.
 * Across a daylight-saving change the offset is checked again at the result, so 20:00 is 20:00 in that zone.
 */
export function zonedToIso(local: string, timeZone: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(local);
  if (!match) throw new Error(`Not a date and time: ${local}`);
  const [, y, mo, d, h, mi] = match.map(Number) as [number, number, number, number, number, number];
  const wall = Date.UTC(y, mo - 1, d, h, mi);
  let instant = wall - offset(wall, timeZone);
  instant = wall - offset(instant, timeZone);
  return new Date(instant).toISOString();
}

/** An ISO instant as the zone's wall clock, for a datetime-local input. */
export function isoToZoned(iso: string, timeZone: string): string {
  const p = parts(Date.parse(iso), timeZone);
  const pad = (n: number | undefined) => String(n ?? 0).padStart(2, '0');
  return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`;
}

/** "Fri, Nov 27, 2026, 8:00 PM CST": readable, in the show's zone, with the zone named. */
export function formatInZone(iso: string, timeZone: string | null | undefined): string {
  return new Intl.DateTimeFormat('en-US', {
    timeZone: timeZone ?? undefined, weekday: 'short', month: 'short', day: 'numeric', year: 'numeric',
    hour: 'numeric', minute: '2-digit', timeZoneName: 'short',
  }).format(new Date(iso));
}
