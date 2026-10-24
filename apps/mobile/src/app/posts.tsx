import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Loading } from '../ui/components';
import { MyPostsScreen } from '../features/submissions/MyPostsScreen';

export default function Posts() {
  const { state } = useSession();
  // While the session restores (a page reload), wait instead of redirecting, so links to this page work.
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <MyPostsScreen /> : <Redirect href="/" />;
}
