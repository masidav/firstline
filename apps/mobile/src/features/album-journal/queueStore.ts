import type { PendingLog, QueueStore } from './pendingLogs';

const KEY = 'fandom-score.journal-queue';

/** Web: localStorage (same page-script caveat as the refresh token, see src/auth/tokenStore.ts). */
export const queueStore: QueueStore = {
  async load() {
    try {
      const value = globalThis.localStorage?.getItem(KEY);
      return value ? (JSON.parse(value) as PendingLog[]) : [];
    } catch {
      return [];
    }
  },
  async save(items) {
    globalThis.localStorage?.setItem(KEY, JSON.stringify(items));
  },
};
