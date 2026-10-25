/**
 * The unread notification count, shared between the home screen's Alerts button and the notifications screen, so
 * reading notifications updates the badge without a reload. A tiny publish/subscribe; no state library needed.
 */
type Listener = (count: number) => void;

const listeners = new Set<Listener>();

export function publishUnread(count: number): void {
  for (const listener of listeners) listener(count);
}

export function subscribeUnread(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
