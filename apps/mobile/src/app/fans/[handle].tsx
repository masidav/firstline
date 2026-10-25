import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { Loading } from '../../ui/components';
import { FanProfileScreen } from '../../features/profiles/FanProfileScreen';

export default function FanProfile() {
  const { state } = useSession();
  const { handle } = useLocalSearchParams<{ handle: string }>();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' && handle ? <FanProfileScreen key={handle} handle={handle} /> : <Redirect href="/" />;
}
