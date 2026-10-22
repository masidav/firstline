import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { StatsScreen } from '../features/stats/StatsScreen';

export default function Stats() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <StatsScreen /> : <Redirect href="/" />;
}
