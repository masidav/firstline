import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ArtistShowsScreen } from '../../../features/ticketing/ArtistShowsScreen';

export default function ArtistShows() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ArtistShowsScreen artistId={artistId} />;
}
