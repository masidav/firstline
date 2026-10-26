import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import type { Visibility } from './journal';

// A journal write made without a connection. It is kept on the device, with the Idempotency-Key of its first
// attempt, until the API accepts it (then the log exists once, however many times it was retried), or until it
// fails for good and the fan discards it. Nothing a fan wrote is dropped quietly.

export interface PendingLogBody {
  rating: number | null;
  review: string | null;
  listened_on: string;
  visibility: Visibility;
}

export interface PendingLog {
  localId: string;
  /** Only this account's entries are sent; another account's wait for it to sign in again. */
  userId: string;
  /** Idempotency-Key for a create; every retry reuses it. */
  key: string;
  kind: 'create' | 'update';
  /** The server log being edited (`update` only). */
  logId?: string;
  albumId: string;
  album: { title: string; artistName: string | null; coverUrl: string | null };
  body: PendingLogBody;
  createdAt: string;
  attempts: number;
  /** Set when the API refused the entry (so retrying would not help); the fan can edit or discard it. */
  failure?: string;
}

/** Where the queue lives between launches: queueStore.ts (web) and queueStore.native.ts. */
export interface QueueStore {
  load(): Promise<PendingLog[]>;
  save(items: PendingLog[]): Promise<void>;
}

/** A failed call that may succeed later: no network, or the API was unable to answer. */
export function isRetryable(error: unknown): boolean {
  if (!(error instanceof ApiError)) {
    return true; // fetch failed: offline, DNS, timeout
  }
  return error.status === 429 || error.status >= 500;
}

/** The browser knows it is offline (web only; native finds out from the failed call). */
export function isKnownOffline(): boolean {
  return typeof navigator !== 'undefined' && navigator.onLine === false;
}

/** Why the API refused an entry, in the fan's words. */
export function failureMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 404) {
      return 'This album or log is no longer on Fandom Score.';
    }
    if (error.code === 'idempotency_conflict') {
      return 'An earlier attempt reached the server with different details. Check your journal before retrying.';
    }
    const field = error.problem.errors?.[0];
    if (field) {
      return `${field.field === 'listened_on' ? 'Listened on' : field.field === 'review' ? 'Review' : field.field}: ${field.message}`;
    }
  }
  return errorMessage(error);
}

/** Seconds to wait before the next automatic attempt. */
export function retryDelaySeconds(attempts: number): number {
  return [5, 15, 60, 300][Math.min(attempts, 3)] ?? 300;
}

/** For tests. */
export function memoryQueueStore(initial: PendingLog[] = []): QueueStore & { items: PendingLog[] } {
  const store = {
    items: initial,
    async load() {
      return store.items;
    },
    async save(items: PendingLog[]) {
      store.items = items;
    },
  };
  return store;
}
