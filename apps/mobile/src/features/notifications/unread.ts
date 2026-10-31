import { useCallback, useEffect, useState } from 'react';

import { unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';

/**
 * The unread notification count, shared between the Profile tab, the Alerts button, and the notifications screen, so reading
 * notifications updates the badge without a reload. A tiny publish/subscribe; no state library needed.
 */
type Listener = (count: number) => void;

const listeners = new Set<Listener>();
let current = 0;

export function publishUnread(count: number): void {
  current = count;
  for (const listener of listeners) listener(count);
}

export function subscribeUnread(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

const POLL_MS = 60_000;

/**
 * The unread notification count. With `poll`, GET /me/notifications every minute (the tabs layout does this once for
 * the app); without it, just the shared value, updated when notifications are read.
 */
export function useUnreadCount(poll: boolean): number {
  const { session } = useSession();
  const [unread, setUnread] = useState(current);

  const load = useCallback(async () => {
    try {
      const page = unwrap(await session.api.GET('/me/notifications', { params: { query: { limit: 1 } } }));
      publishUnread(page.unread_count ?? 0);
    } catch {
      // The badge is a hint; the screen itself shows errors.
    }
  }, [session]);

  useEffect(() => subscribeUnread(setUnread), []);
  useEffect(() => {
    if (!poll) return;
    void load();
    const timer = setInterval(() => void load(), POLL_MS);
    return () => clearInterval(timer);
  }, [poll, load]);

  return unread;
}
