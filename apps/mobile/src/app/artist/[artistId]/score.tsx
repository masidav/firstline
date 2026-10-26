import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ScoreScreen } from '../../../features/fandom-score/ScoreScreen';
import { Loading } from '../../../ui/components';

export default function ArtistScore() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ScoreScreen artistId={artistId} />;
}
