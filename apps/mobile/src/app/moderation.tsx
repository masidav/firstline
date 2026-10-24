import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { ModerationScreen } from '../features/moderation/ModerationScreen';
import { Loading } from '../ui/components';

/** Staff only; everyone else goes home (the API refuses non-staff anyway). */
export default function Moderation() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <ModerationScreen /> : <Redirect href="/home" />;
}
