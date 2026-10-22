import * as Crypto from 'expo-crypto';

import type { components } from '../../api/client';

export type Album = components['schemas']['Album'];
export type AlbumDetail = components['schemas']['AlbumDetail'];
export type AlbumLog = components['schemas']['AlbumLog'];
export type LogResult = components['schemas']['LogResult'];
export type PointsAward = components['schemas']['PointsAward'];
export type Visibility = components['schemas']['Visibility'];

export const VISIBILITIES: { value: Visibility; label: string }[] = [
  { value: 'public', label: 'Public' },
  { value: 'followers', label: 'Followers' },
  { value: 'private', label: 'Private' },
];

/** Ratings are half-star steps from 1 (½ star) to 10 (5 stars). */
export const RATINGS = Array.from({ length: 10 }, (_, i) => i + 1);

export function formatRating(rating: number | null | undefined): string | undefined {
  if (rating == null) return undefined;
  const stars = rating / 2;
  return `${Number.isInteger(stars) ? stars : stars.toFixed(1)} ★`;
}

export function ratingLabel(rating: number): string {
  const stars = rating / 2;
  return Number.isInteger(stars) ? `${stars}` : `${Math.floor(stars)}½`;
}

/** "2007 · Album" (the year only: Spotify's precision is often just the year). */
export function describeAlbum(album: Pick<Album, 'release_date' | 'album_type'>): string {
  const year = album.release_date?.slice(0, 4);
  const type = album.album_type.charAt(0).toUpperCase() + album.album_type.slice(1);
  return year ? `${year} · ${type}` : type;
}

/** Link back to the album on Spotify, required whenever its metadata or artwork is shown (ADR 0006). */
export function spotifyAlbumUrl(spotifyAlbumId: string | null | undefined): string | undefined {
  return spotifyAlbumId ? `https://open.spotify.com/album/${spotifyAlbumId}` : undefined;
}

/** Today's date as YYYY-MM-DD in the device's local time zone. */
export function todayLocal(now = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/** A fresh Idempotency-Key for one attempt at a create call; retries of the same attempt reuse it. */
export function idempotencyKey(): string {
  return Crypto.randomUUID();
}

/** "You earned 7 points: first log, review." */
export function describePoints(awards: PointsAward[]): string | undefined {
  if (awards.length === 0) return undefined;
  const total = awards.reduce((sum, award) => sum + award.points, 0);
  const reasons = awards.map((award) => AWARD_NAMES[award.rule_key] ?? award.rule_key);
  return `You earned ${Number.isInteger(total) ? total : total.toFixed(2)} points: ${reasons.join(', ')}.`;
}

const AWARD_NAMES: Record<string, string> = {
  album_logged: 'first log',
  album_relisten: 'relisten',
  review_written: 'review',
};
