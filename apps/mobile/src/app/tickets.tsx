import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { MyTicketsScreen } from '../features/ticketing/MyTicketsScreen';

export default function Tickets() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <MyTicketsScreen /> : <Redirect href="/" />;
}
