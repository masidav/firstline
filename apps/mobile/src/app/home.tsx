import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { HomeScreen } from '../features/home/HomeScreen';

export default function Home() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <HomeScreen /> : <Redirect href="/" />;
}
