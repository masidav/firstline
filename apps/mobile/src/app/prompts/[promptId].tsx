import { Redirect, useLocalSearchParams } from 'expo-router';

import { useSession } from '../../auth/SessionProvider';
import { Loading } from '../../ui/components';
import { PromptAnswersScreen } from '../../features/prompts/PromptAnswersScreen';

export default function PromptAnswers() {
  const { state } = useSession();
  const { promptId } = useLocalSearchParams<{ promptId: string }>();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' && promptId ? <PromptAnswersScreen promptId={promptId} /> : <Redirect href="/" />;
}
