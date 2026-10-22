import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { LogScreen } from '../../features/album-journal/LogScreen';

export default function Log() {
  const { state } = useSession();
  const { logId } = useLocalSearchParams<{ logId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <LogScreen logId={logId} />;
}
