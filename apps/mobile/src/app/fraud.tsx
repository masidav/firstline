import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { FraudReviewScreen } from '../features/fraud-review/FraudReviewScreen';
import { Loading } from '../ui/components';

/** Staff only; everyone else goes home (the API refuses non-staff anyway). */
export default function Fraud() {
  const { state } = useSession();
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  return state.me.is_staff ? <FraudReviewScreen /> : <Redirect href="/home" />;
}
