import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { ShowScreen } from '../../features/ticketing/ShowScreen';
import { Loading } from '../../ui/components';

export default function Show() {
  const { state } = useSession();
  const { eventId } = useLocalSearchParams<{ eventId: string }>();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ShowScreen eventId={eventId} />;
}
