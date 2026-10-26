import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { FeaturedAdminScreen } from '../features/featured/FeaturedAdminScreen';

export default function FeaturedAdmin() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <FeaturedAdminScreen /> : <Redirect href="/home" />;
}
