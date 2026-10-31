import { Redirect } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { JournalScreen } from '../../features/album-journal/JournalScreen';
import { Loading } from '../../ui/components';

export default function Journal() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <JournalScreen /> : <Redirect href="/" />;
}
