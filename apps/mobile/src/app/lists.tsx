import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { ListsScreen } from '../features/lists/ListsScreen';

export default function Lists() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <ListsScreen /> : <Redirect href="/" />;
}
