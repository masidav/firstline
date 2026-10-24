import { manipulateAsync, SaveFormat } from 'expo-image-manipulator';

import { type Api, type components, unwrap } from '../../api/client';

export type Submission = components['schemas']['Submission'];
export type SubmissionKind = components['schemas']['SubmissionKind'];

/** The kinds a fan can post, in the order the app offers them. Screenshots are proof-only and not offered yet. */
export const POST_KINDS: { kind: SubmissionKind; label: string; hint: string }[] = [
  { kind: 'concert_photo', label: 'Concert photo', hint: 'A photo you took at the show.' },
  { kind: 'merch', label: 'Merch', hint: 'Merch you bought: a shirt, a record, a poster.' },
  { kind: 'ticket', label: 'Ticket', hint: 'Proof only: tickets are never shown to other fans.' },
];

export const MAX_WIDTH = 2048;

/**
 * Re-encodes the picked photo as a JPEG at most MAX_WIDTH wide. Re-encoding bakes in the rotation and drops EXIF
 * (location, camera details); the server strips metadata again regardless.
 */
export async function preparePhoto(uri: string, width?: number): Promise<string> {
  const actions = width && width > MAX_WIDTH ? [{ resize: { width: MAX_WIDTH } }] : [];
  const result = await manipulateAsync(uri, actions, { compress: 0.85, format: SaveFormat.JPEG });
  return result.uri;
}

/** Uploads one prepared JPEG: asks the API for an upload slot, then PUTs the bytes to its signed link. */
export async function uploadPhoto(api: Api, uri: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const blob = await (await fetchImpl(uri)).blob();
  const intent = unwrap(
    await api.POST('/uploads', { body: { purpose: 'submission', mime_type: 'image/jpeg', bytes: blob.size } }),
  );
  // The link is signed, so no sign-in header (like a storage provider's presigned URL).
  const put = await fetchImpl(intent.url, { method: intent.method, headers: intent.headers ?? {}, body: blob });
  if (!put.ok) {
    throw new Error(put.status === 422 ? "That photo couldn't be read. Try another one." : `Upload failed (${put.status}).`);
  }
  return intent.upload_id;
}

export type ConcertInput = { date: string; venue: string; city: string };

/** A concert photo is pinned to a listed show or to a concert the fan enters. */
export type ConcertPin = { eventId: string } | { concert: ConcertInput };

/** Uploads the photo and submits it as proof for the artist. */
export async function postPhoto(
  api: Api,
  input: { artistId: string; kind: SubmissionKind; caption: string; photoUri: string; pin?: ConcertPin },
  fetchImpl: typeof fetch = fetch,
): Promise<Submission> {
  const uploadId = await uploadPhoto(api, input.photoUri, fetchImpl);
  return unwrap(
    await api.POST('/submissions', {
      body: {
        artist_id: input.artistId,
        kind: input.kind,
        upload_ids: [uploadId],
        caption: input.caption.trim() || null,
        ...(input.pin && 'eventId' in input.pin ? { event_id: input.pin.eventId } : {}),
        ...(input.pin && 'concert' in input.pin
          ? { concert: { date: input.pin.concert.date, venue: input.pin.concert.venue.trim(), city: input.pin.concert.city.trim() } }
          : {}),
      },
    }),
  );
}

/** A typed-in concert is complete: a real YYYY-MM-DD date that isn't in the future, a venue, and a city. */
export function concertComplete(concert: ConcertInput, today = new Date()): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(concert.date.trim());
  if (!match || !concert.venue.trim() || !concert.city.trim()) return false;
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  const valid = date.toISOString().slice(0, 10) === concert.date.trim();
  return valid && date.getTime() <= today.getTime() + 24 * 3600 * 1000 && date.getUTCFullYear() >= 1950;
}

/** "Oct 3, 2026" for a date or date-time string, in UTC so the day doesn't shift. */
export function formatDate(value: string): string {
  return new Date(value.length === 10 ? `${value}T00:00:00Z` : value).toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** "Metro, Chicago · Oct 3, 2026": where and when, from a listed show or the concert the fan entered. */
export function concertLine(place: { name?: string | null; venue?: string | null; city?: string | null; date: string }): string {
  const where = [place.venue, place.city].filter(Boolean).join(', ') || place.name || 'Concert';
  return `${where} · ${formatDate(place.date)}`;
}

export function submissionConcertLine(submission: Submission): string | null {
  if (submission.event) {
    const { name, venue, city, starts_at } = submission.event;
    return concertLine({ name, venue, city, date: starts_at });
  }
  return submission.concert ? concertLine(submission.concert) : null;
}

export function kindLabel(kind: SubmissionKind): string {
  return POST_KINDS.find((k) => k.kind === kind)?.label ?? 'Screenshot';
}

export function statusLabel(submission: Submission): string {
  switch (submission.status) {
    case 'verified':
      if (submission.removed) return 'Verified · taken off the feed by staff';
      return submission.shared_at ? 'Verified · on the feed' : 'Verified';
    case 'rejected':
      return submission.rejection_reason ? `Not accepted: ${submission.rejection_reason}` : 'Not accepted';
    default:
      return 'Waiting for review';
  }
}
