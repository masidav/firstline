import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { config } from '../config';
import { DevSignInForm } from '../features/authentication/DevSignInForm';
import { Body, Screen, Title } from '../ui/components';

export default function SignIn() {
  const { session, state } = useSession();
  if (state.status !== 'signedOut') {
    return <Redirect href="/" />;
  }

  return (
    <Screen>
      <Title>Fandom Score</Title>
      <Body muted>Prove you're a real fan. Get first access to fair, face-value tickets.</Body>
      {config.devSignIn ? (
        <DevSignInForm signIn={(idToken, handle) => session.signIn('google', idToken, handle)} />
      ) : (
        <Body>No sign-in method is configured for this build yet.</Body>
      )}
    </Screen>
  );
}
