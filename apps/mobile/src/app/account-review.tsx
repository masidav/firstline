import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { AccountReviewScreen } from '../features/fraud-review/AccountReviewScreen';
import { Loading } from '../ui/components';

export default function AccountReview() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  return state.status === 'signedIn' ? <AccountReviewScreen /> : <Redirect href="/" />;
}
