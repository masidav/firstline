import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { SuspensionsScreen } from '../features/moderation/SuspensionsScreen';
import { Loading } from '../ui/components';

/** Staff only; everyone else goes home (the API refuses non-staff anyway). */
export default function Suspensions() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <SuspensionsScreen /> : <Redirect href="/home" />;
}
