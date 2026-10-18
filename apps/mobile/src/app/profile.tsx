import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { ProfileScreen } from '../features/settings/ProfileScreen';

export default function Profile() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <ProfileScreen me={state.me} /> : <Redirect href="/" />;
}
