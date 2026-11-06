import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { MusicScreen } from '../features/music/MusicScreen';
import { Loading } from '../ui/components';

export default function Music() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <MusicScreen /> : <Redirect href="/" />;
}
