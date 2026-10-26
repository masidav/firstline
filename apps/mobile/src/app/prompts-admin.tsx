import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { PromptsAdminScreen } from '../features/prompts/PromptsAdminScreen';

export default function PromptsAdmin() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <PromptsAdminScreen /> : <Redirect href="/home" />;
}
