import { Redirect, router } from 'expo-router';
import { useEffect, useState } from 'react';

import { useSession } from '../auth/SessionProvider';
import { takePendingGoogleSignIn } from '../features/authentication/googleSignIn';
import { completeGoogleSignIn, parseGoogleRedirect, type SignInResult } from '../features/authentication/providerSignIn';
import { SignInScreen } from '../features/authentication/SignInScreen';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../ui/components';

/**
 * Google redirects here on the web (#id_token=&state=, or ?error=). The fragment is read once, as the page
 * loads, and dropped from the address bar so the token is not left in the browser history.
 */
export default function GoogleCallback() {
  const { state } = useSession();
  const [result, setResult] = useState<SignInResult | null>(null);

  useEffect(() => {
    const params = parseGoogleRedirect(window.location.href);
    let cancelled = false;
    void completeGoogleSignIn(params, takePendingGoogleSignIn()).then((outcome) => {
      // By now (a microtask after this commit) the router has written its first URL, which keeps the browser's
      // hash; clearing it here sticks, since later navigations build their URLs without this route's params.
      window.history.replaceState(null, '', window.location.pathname);
      if (!cancelled) setResult(outcome);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  if (state.status === 'signedIn') {
    return <Redirect href="/" />;
  }
  if (result === null) {
    return <Loading />;
  }
  if (result.ok) {
    return <SignInScreen initialCredential={result.credential} />;
  }
  return (
    <Screen>
      <Title>Not signed in</Title>
      <ErrorText>{result.message}</ErrorText>
      <Body muted>Nothing was saved.</Body>
      <Button label="Back to sign-in" onPress={() => router.replace('/sign-in')} />
    </Screen>
  );
}
