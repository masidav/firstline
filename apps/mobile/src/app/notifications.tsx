import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { NotificationsScreen } from '../features/notifications/NotificationsScreen';

export default function Notifications() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <NotificationsScreen /> : <Redirect href="/" />;
}
