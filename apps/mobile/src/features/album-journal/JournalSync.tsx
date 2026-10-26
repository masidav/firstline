import { useSession } from '../../auth/SessionProvider';
import { useJournalSync } from './journalQueue';

/** Mounted once under SessionProvider: sends queued journal writes whenever there is a signed-in connection. */
export function JournalSync() {
  const { session, state } = useSession();
  useJournalSync(session, state);
  return null;
}
