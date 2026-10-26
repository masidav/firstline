import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ArtistAlbumsScreen } from '../../../features/album-journal/ArtistAlbumsScreen';
import { Loading } from '../../../ui/components';

export default function ArtistAlbums() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ArtistAlbumsScreen artistId={artistId} />;
}
