import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { AlbumScreen } from '../../features/album-journal/AlbumScreen';

export default function Album() {
  const { state } = useSession();
  const { albumId } = useLocalSearchParams<{ albumId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <AlbumScreen albumId={albumId} />;
}
