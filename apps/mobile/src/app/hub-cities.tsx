import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { HubCitiesScreen } from '../features/hub-cities/HubCitiesScreen';

export default function HubCities() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <HubCitiesScreen me={state.me} /> : <Redirect href="/" />;
}
