import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ArtistShowsScreen } from '../../../features/ticketing/ArtistShowsScreen';
import { Loading } from '../../../ui/components';

export default function ArtistShows() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ArtistShowsScreen artistId={artistId} />;
}
