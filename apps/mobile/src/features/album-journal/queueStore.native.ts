import { File, Paths } from 'expo-file-system';

import type { PendingLog, QueueStore } from './pendingLogs';

/** iOS/Android: a JSON file in the app's documents directory (private to the app, backed up with it). */
export const queueStore: QueueStore = {
  async load() {
    try {
      const file = new File(Paths.document, 'journal-queue.json');
      return file.exists ? (JSON.parse(await file.text()) as PendingLog[]) : [];
    } catch {
      return [];
    }
  },
  async save(items) {
    const file = new File(Paths.document, 'journal-queue.json');
    file.write(JSON.stringify(items));
  },
};
