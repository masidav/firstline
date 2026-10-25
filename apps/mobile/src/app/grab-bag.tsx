import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { GrabBagScreen } from '../features/grab-bag/GrabBagScreen';

export default function GrabBag() {
  const { state } = useSession();
  // While the session restores (a page reload), wait instead of redirecting, so links to this page work.
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <GrabBagScreen /> : <Redirect href="/" />;
}
