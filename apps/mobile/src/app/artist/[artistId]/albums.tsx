import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ArtistAlbumsScreen } from '../../../features/album-journal/ArtistAlbumsScreen';

export default function ArtistAlbums() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ArtistAlbumsScreen artistId={artistId} />;
}
