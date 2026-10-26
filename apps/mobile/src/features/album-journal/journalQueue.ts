import { useEffect, useMemo, useSyncExternalStore } from 'react';
import { AppState } from 'react-native';

import { type Api, ApiError, unwrap } from '../../api/client';
import type { SessionManager, SessionState } from '../../auth/session';
import { idempotencyKey, type LogResult } from './journal';
import { failureMessage, isRetryable, type PendingLog, type PendingLogBody, type QueueStore, retryDelaySeconds } from './pendingLogs';
import { queueStore } from './queueStore';

export type SyncedListener = (entry: PendingLog, result: LogResult) => void;

/**
 * The queue of journal writes waiting for a connection, and the sync that sends them.
 *
 * Entries go out one at a time, oldest first, as soon as the app is signed in, then again on every reconnect,
 * foreground, new entry, or "Retry" press, and on a backoff timer after a failed attempt. A create keeps its
 * Idempotency-Key, so a retry after a lost response does not log the album twice. An entry the API refuses
 * (validation, deleted album) stays with its reason until the fan edits or discards it.
 */
export class JournalQueue {
  private items: PendingLog[] = [];
  private visible: PendingLog[] = [];
  private loaded: Promise<void> | null = null;
  private api: Api | null = null;
  private userId: string | null = null;
  private flushing: Promise<void> | null = null;
  private again = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private readonly listeners = new Set<() => void>();
  private readonly syncedListeners = new Set<SyncedListener>();

  constructor(
    private readonly store: QueueStore,
    private readonly newId: () => string = idempotencyKey,
  ) {}

  /** Signed in: send this account's entries now, and whenever something changes. */
  async start(api: Api, userId: string): Promise<void> {
    this.api = api;
    this.userId = userId;
    await this.load();
    this.refreshVisible();
    void this.flush();
  }

  /** Signed out: nothing is sent; the entries stay for the next sign-in. */
  stop(): void {
    this.api = null;
    this.userId = null;
    this.clearTimer();
    this.refreshVisible();
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** Called when an entry reaches the API (with what the API returned). */
  onSynced(listener: SyncedListener): () => void {
    this.syncedListeners.add(listener);
    return () => this.syncedListeners.delete(listener);
  }

  /** The signed-in account's entries, oldest first. The same array until something changes. */
  getPending = (): PendingLog[] => this.visible;

  async add(input: {
    userId: string;
    kind: 'create' | 'update';
    logId?: string;
    albumId: string;
    album: PendingLog['album'];
    body: PendingLogBody;
    /** The Idempotency-Key of the attempt that just failed, so the queue's retries count as the same attempt. */
    key?: string;
  }): Promise<PendingLog> {
    await this.load();
    const entry: PendingLog = {
      localId: this.newId(),
      createdAt: new Date().toISOString(),
      attempts: 0,
      ...input,
      key: input.key ?? this.newId(),
    };
    // One pending edit per server log: a second edit replaces the first.
    const items = input.kind === 'update' ? this.items.filter((item) => !(item.kind === 'update' && item.logId === input.logId)) : this.items;
    await this.commit([...items, entry]);
    this.kick();
    return entry;
  }

  /** Changes what a waiting entry will send; a failed entry gets another try. */
  async edit(localId: string, body: PendingLogBody): Promise<PendingLog | undefined> {
    await this.load();
    const entry = this.items.find((item) => item.localId === localId);
    if (!entry) return undefined;
    const edited: PendingLog = { ...entry, body, attempts: 0, failure: undefined };
    await this.commit(this.items.map((item) => (item.localId === localId ? edited : item)));
    this.kick();
    return edited;
  }

  async discard(localId: string): Promise<void> {
    await this.load();
    await this.commit(this.items.filter((item) => item.localId !== localId));
  }

  /** "Retry now": failed entries get another go, and waiting ones are sent at once. */
  async retry(): Promise<void> {
    await this.load();
    await this.commit(this.items.map((item) => (item.failure ? { ...item, failure: undefined, attempts: 0 } : item)));
    this.kick();
    await this.flushing;
  }

  /**
   * Sends what is waiting, one entry at a time. A call during a round joins that round: after a failed attempt
   * the backoff timer decides when to try again. New work (add, edit, retry) asks for one more round instead.
   */
  flush(): Promise<void> {
    if (this.flushing) {
      return this.flushing;
    }
    this.again = false;
    this.flushing = this.doFlush().finally(() => {
      this.flushing = null;
      if (this.again) {
        this.again = false;
        void this.flush();
      }
    });
    return this.flushing;
  }

  /** New work: send now, or once the round in progress is over. */
  private kick(): void {
    if (this.flushing) {
      this.again = true;
    } else {
      void this.flush();
    }
  }

  private async doFlush(): Promise<void> {
    this.clearTimer();
    await this.load();
    for (;;) {
      const api = this.api;
      const entry = this.items.find((item) => item.userId === this.userId && !item.failure);
      if (!api || !entry) return;

      try {
        const result = await this.send(api, entry);
        await this.commit(this.items.filter((item) => item.localId !== entry.localId));
        this.syncedListeners.forEach((listener) => listener(entry, result));
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          return; // The session ended; the next sign-in picks the queue up.
        }
        if (isRetryable(error)) {
          const attempts = entry.attempts + 1;
          await this.commit(this.items.map((item) => (item.localId === entry.localId ? { ...item, attempts } : item)));
          this.scheduleRetry(retryDelaySeconds(attempts));
          return;
        }
        await this.commit(
          this.items.map((item) => (item.localId === entry.localId ? { ...item, failure: failureMessage(error) } : item)),
        );
      }
    }
  }

  private send(api: Api, entry: PendingLog): Promise<LogResult> {
    const { rating, review, listened_on, visibility } = entry.body;
    if (entry.kind === 'update' && entry.logId) {
      return api
        .PATCH('/logs/{logId}', { params: { path: { logId: entry.logId } }, body: { rating, review, listened_on, visibility } })
        .then(unwrap);
    }
    return api
      .POST('/logs', {
        headers: { 'Idempotency-Key': entry.key },
        body: {
          album_id: entry.albumId,
          ...(rating != null ? { rating } : {}),
          ...(review ? { review } : {}),
          listened_on,
          visibility,
        },
      })
      .then(unwrap);
  }

  private scheduleRetry(seconds: number): void {
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.flush();
    }, seconds * 1000);
  }

  private clearTimer(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private load(): Promise<void> {
    this.loaded ??= this.store.load().then((items) => {
      this.items = items;
      this.refreshVisible();
    });
    return this.loaded;
  }

  private async commit(items: PendingLog[]): Promise<void> {
    this.items = items;
    this.refreshVisible();
    try {
      await this.store.save(items);
    } catch {
      // Out of space or storage blocked: the entries still live in memory for this session.
    }
  }

  private refreshVisible(): void {
    this.visible = this.items.filter((item) => item.userId === this.userId);
    this.listeners.forEach((listener) => listener());
  }
}

export const journalQueue = new JournalQueue(queueStore);

/** The signed-in fan's waiting entries, for one album or all. */
export function usePendingLogs(albumId?: string, queue: JournalQueue = journalQueue): PendingLog[] {
  const all = useSyncExternalStore(queue.subscribe, queue.getPending, queue.getPending);
  return useMemo(() => (albumId ? all.filter((entry) => entry.albumId === albumId) : all), [all, albumId]);
}

/** Runs `listener` whenever an entry reaches the API. */
export function useSyncedLogs(listener: SyncedListener, queue: JournalQueue = journalQueue): void {
  useEffect(() => queue.onSynced(listener), [listener, queue]);
}

/**
 * Keeps the queue going with the session: starts it on sign-in, stops it on sign-out, and sends again when the
 * browser comes back online or the app returns to the foreground. Mounted once in the root layout.
 */
export function useJournalSync(session: SessionManager, state: SessionState, queue: JournalQueue = journalQueue): void {
  const userId = state.status === 'signedIn' ? state.me.id : null;
  useEffect(() => {
    if (!userId) {
      queue.stop();
      return;
    }
    void queue.start(session.api, userId);
    const online = () => void queue.flush();
    const foreground = AppState.addEventListener('change', (next) => {
      if (next === 'active') online();
    });
    globalThis.addEventListener?.('online', online);
    return () => {
      foreground.remove();
      globalThis.removeEventListener?.('online', online);
    };
  }, [session, userId, queue]);
}
