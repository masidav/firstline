import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { ShowScreen } from '../../features/ticketing/ShowScreen';

export default function Show() {
  const { state } = useSession();
  const { eventId } = useLocalSearchParams<{ eventId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ShowScreen eventId={eventId} />;
}
