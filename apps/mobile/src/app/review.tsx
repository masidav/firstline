import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { ReviewScreen } from '../features/submissions/ReviewScreen';

/** Staff only; everyone else goes home (the API refuses non-staff anyway). */
export default function Review() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <ReviewScreen /> : <Redirect href="/home" />;
}
