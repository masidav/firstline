import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { StatsScreen } from '../features/stats/StatsScreen';
import { Loading } from '../ui/components';

export default function Stats() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <StatsScreen /> : <Redirect href="/" />;
}
