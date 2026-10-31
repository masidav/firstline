import { Redirect } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { ProfileScreen } from '../../features/settings/ProfileScreen';
import { Loading } from '../../ui/components';

export default function Profile() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <ProfileScreen me={state.me} /> : <Redirect href="/" />;
}
