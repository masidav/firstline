import { Redirect } from 'expo-router';

import { useSession } from '../auth/SessionProvider';
import { Body, Button, Loading, Screen, Title } from '../ui/components';

/** Routes by session state: loading, can't reach the API, signed out, or signed in. */
export default function Index() {
  const { session, state } = useSession();

  switch (state.status) {
    case 'loading':
      return <Loading />;
    case 'unreachable':
      return (
        <Screen>
          <Title>Can't reach the server</Title>
          <Body muted>Check your connection, or that the API is running.</Body>
          <Button label="Try again" onPress={() => void session.restore()} />
        </Screen>
      );
    case 'signedOut':
      return <Redirect href="/sign-in" />;
    case 'signedIn':
      return <Redirect href="/home" />;
  }
}
