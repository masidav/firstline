import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { HomeScreen } from '../features/home/HomeScreen';

export default function Home() {
  const { state } = useSession();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  // A hub city is required before anything else (decided 2026-10-20).
  return state.me.hub_cities.length === 0 ? <Redirect href="/hub-cities" /> : <HomeScreen />;
}
