import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { ListScreen } from '../../features/lists/ListScreen';

export default function List() {
  const { state } = useSession();
  const { listId } = useLocalSearchParams<{ listId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ListScreen listId={listId} />;
}
