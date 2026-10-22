import type { Api } from '../../api/client';
import { ApiError, unwrap } from '../../api/client';
import type { LogResult } from './journal';

/**
 * Logs written while offline. Each entry keeps the Idempotency-Key of its first attempt, so sending it later
 * (or twice) can never log the album twice. Entries the API rejects stay with the reason until the fan discards
 * them; nothing is dropped silently.
 */
export interface PendingLog {
  key: string;
  albumId: string;
  albumTitle: string;
  body: Record<string, unknown>;
  createdAt: string;
  /** Set when the API answered with an error (not a network failure); the entry waits for the fan. */
  failure?: string;
}

export interface OutboxStorage {
  read(): string | null;
  write(value: string): void;
}

const STORAGE_KEY = 'fandom-score.log-outbox';

/** Browser storage when there is one, else memory (the native app will plug in its own store). */
export function defaultStorage(): OutboxStorage {
  try {
    const local = globalThis.localStorage;
    if (local) {
      return {
        read: () => local.getItem(STORAGE_KEY),
        write: (value) => local.setItem(STORAGE_KEY, value),
      };
    }
  } catch {
    // Storage blocked (private mode): fall through to memory.
  }
  let memory: string | null = null;
  return { read: () => memory, write: (value) => (memory = value) };
}

export class Outbox {
  constructor(private readonly storage: OutboxStorage = defaultStorage()) {}

  list(): PendingLog[] {
    try {
      const raw = this.storage.read();
      return raw ? (JSON.parse(raw) as PendingLog[]) : [];
    } catch {
      return [];
    }
  }

  add(entry: PendingLog): void {
    this.save([...this.list().filter((e) => e.key !== entry.key), entry]);
  }

  remove(key: string): void {
    this.save(this.list().filter((e) => e.key !== key));
  }

  /** Marks an entry as rejected by the API, keeping it for the fan to see. */
  fail(key: string, failure: string): void {
    this.save(this.list().map((e) => (e.key === key ? { ...e, failure } : e)));
  }

  /**
   * Sends every entry that is not already marked failed. Network failures leave entries waiting; API errors mark
   * them failed. Returns the results of the ones that got through.
   */
  async flush(api: Api): Promise<LogResult[]> {
    const sent: LogResult[] = [];
    for (const entry of this.list()) {
      if (entry.failure) continue;
      try {
        const result = unwrap(
          await api.POST('/logs', { headers: { 'Idempotency-Key': entry.key }, body: entry.body as never }),
        );
        this.remove(entry.key);
        sent.push(result);
      } catch (e) {
        if (e instanceof ApiError) {
          this.fail(entry.key, e.problem.errors?.[0]?.message ?? e.message);
        }
        // Otherwise still offline: try again next time.
      }
    }
    return sent;
  }

  private save(entries: PendingLog[]): void {
    try {
      this.storage.write(JSON.stringify(entries));
    } catch {
      // Nothing sensible to do without storage; the in-memory copy is what we have.
    }
  }
}

let shared: Outbox | undefined;

/** The app's outbox (one per process). */
export function outbox(): Outbox {
  shared ??= new Outbox();
  return shared;
}

/** True for failures that mean "no connection", as opposed to an answer from the API. */
export function isNetworkFailure(error: unknown): boolean {
  return !(error instanceof ApiError);
}
