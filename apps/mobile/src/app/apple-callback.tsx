import { Redirect, router } from 'expo-router';
import { useEffect, useState } from 'react';

import { useSession } from '../auth/SessionProvider';
import { takePendingAppleSignIn } from '../features/authentication/appleSignIn';
import { completeAppleSignIn, parseProviderRedirect, type SignInResult } from '../features/authentication/providerSignIn';
import { SignInScreen } from '../features/authentication/SignInScreen';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../ui/components';

/**
 * The API sends the browser here after Apple's form post, on the web (#id_token=&state=, or #error=&state=). The
 * fragment is read once, as the page loads, and dropped from the address bar so the token is not left in the
 * browser history. The same shape as /google-callback.
 */
export default function AppleCallback() {
  const { state } = useSession();
  const [result, setResult] = useState<SignInResult | null>(null);

  useEffect(() => {
    const outcome = completeAppleSignIn(parseProviderRedirect(window.location.href), takePendingAppleSignIn());
    let cancelled = false;
    // A microtask after this commit: by then the router has written its first URL, which keeps the browser's hash;
    // clearing it here sticks, since later navigations build their URLs without this route's params.
    void Promise.resolve().then(() => {
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
