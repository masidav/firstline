import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { SignInScreen } from '../features/authentication/SignInScreen';

export default function SignIn() {
  const { state } = useSession();
  if (state.status !== 'signedOut') {
    return <Redirect href="/" />;
  }
  return <SignInScreen />;
}
