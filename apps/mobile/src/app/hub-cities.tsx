import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { HubCitiesScreen } from '../features/hub-cities/HubCitiesScreen';
import { Loading } from '../ui/components';

export default function HubCities() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <HubCitiesScreen me={state.me} /> : <Redirect href="/" />;
}
