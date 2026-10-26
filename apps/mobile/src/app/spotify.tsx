import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { SpotifyScreen } from '../features/spotify/SpotifyScreen';
import { Loading } from '../ui/components';

export default function Spotify() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <SpotifyScreen /> : <Redirect href="/" />;
}
