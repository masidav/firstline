import { createContext, type ReactNode, useContext, useEffect, useState, useSyncExternalStore } from 'react';

import { config } from '../config';
import { SessionManager, type SessionState } from './session';
import { tokenStore } from './tokenStore';

const SessionContext = createContext<SessionManager | null>(null);

export function SessionProvider({ children, manager }: { children: ReactNode; manager?: SessionManager }) {
  const [session] = useState(() => manager ?? new SessionManager(config.apiUrl, tokenStore));

  useEffect(() => {
    void session.restore();
  }, [session]);

  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

export function useSession(): { session: SessionManager; state: SessionState } {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error('useSession must be used inside SessionProvider');
  }
  const state = useSyncExternalStore(session.subscribe, session.getState, session.getState);
  return { session, state };
}
