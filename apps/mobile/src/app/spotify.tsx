import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { SpotifyScreen } from '../features/spotify/SpotifyScreen';

export default function Spotify() {
  const { state } = useSession();
  return state.status === 'signedIn' ? <SpotifyScreen /> : <Redirect href="/" />;
}
