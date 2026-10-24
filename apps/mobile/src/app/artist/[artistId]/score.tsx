import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../../auth/SessionProvider';
import { ScoreScreen } from '../../../features/fandom-score/ScoreScreen';

export default function ArtistScore() {
  const { state } = useSession();
  const { artistId } = useLocalSearchParams<{ artistId: string }>();
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return <ScoreScreen artistId={artistId} />;
}
