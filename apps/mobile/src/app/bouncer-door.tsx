import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { BouncerDoorScreen } from '../features/grab-bag/BouncerDoorScreen';

export default function BouncerDoor() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <BouncerDoorScreen /> : <Redirect href="/" />;
}
