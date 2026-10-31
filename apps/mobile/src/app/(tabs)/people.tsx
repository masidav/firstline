import { Redirect } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { Loading } from '../../ui/components';
import { PeopleScreen } from '../../features/follows/PeopleScreen';

export default function People() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <PeopleScreen /> : <Redirect href="/" />;
}
