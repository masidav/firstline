import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { AlbumScreen } from '../../features/album-journal/AlbumScreen';
import { Loading } from '../../ui/components';

export default function Album() {
  const { state } = useSession();
  const { albumId } = useLocalSearchParams<{ albumId: string }>();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <AlbumScreen albumId={albumId} />;
}
