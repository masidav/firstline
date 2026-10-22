import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { JournalScreen } from '../features/album-journal/JournalScreen';

export default function Journal() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <JournalScreen /> : <Redirect href="/" />;
}
