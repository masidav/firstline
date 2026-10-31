import { useEffect, useState } from 'react';

import type { Credential } from '../../auth/session';
import { useSession } from '../../auth/SessionProvider';
import { config } from '../../config';
import { Body, Button, ErrorText, Screen, Title } from '../../ui/components';
import { isAppleSignInAvailable, startAppleSignIn } from './appleSignIn';
import { AppleSignInButton } from './AppleSignInButton';
import { randomString } from './crypto';
import { DevSignInForm } from './DevSignInForm';
import { FinishSignIn } from './FinishSignIn';
import { googleClientId, startGoogleSignIn } from './googleSignIn';
import type { SignInOutcome } from './providerSignIn';

/**
 * Sign in with Google (web and native) or Apple (iOS), plus the developer sign-in when the build enables it.
 * Signing in sets the session state; the route redirects once it is `signedIn`.
 */
export function SignInScreen({ initialCredential = null }: { initialCredential?: Credential | null }) {
  const { session } = useSession();
  const [credential, setCredential] = useState<Credential | null>(initialCredential);
  const [busy, setBusy] = useState<'google' | 'apple' | null>(null);
  const [error, setError] = useState<string>();
  const [appleAvailable, setAppleAvailable] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void isAppleSignInAvailable().then((available) => {
      if (!cancelled) setAppleAvailable(available);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function start(provider: 'google' | 'apple', run: () => Promise<SignInOutcome>) {
    setBusy(provider);
    setError(undefined);
    let outcome: SignInOutcome;
    try {
      outcome = await run();
    } catch (e) {
      outcome = { ok: false, message: e instanceof Error ? e.message : 'Something went wrong. Try again.' };
    }
    if (outcome === 'redirecting') {
      return; // The page is leaving; the callback route takes over.
    }
    setBusy(null);
    if (outcome === 'cancelled') {
      return;
    }
    if (outcome.ok) {
      setCredential(outcome.credential);
    } else {
      setError(outcome.message);
    }
  }

  const providers = Boolean(googleClientId) || appleAvailable;

  return (
    <Screen>
      <Title>FirstLine</Title>
      <Body muted>Prove you're a real fan. Get first access to fair, face-value tickets.</Body>
      {credential ? (
        <FinishSignIn credential={credential} onCancel={() => setCredential(null)} />
      ) : (
        <>
          {googleClientId ? (
            <Button
              label="Continue with Google"
              onPress={() => void start('google', startGoogleSignIn)}
              busy={busy === 'google'}
              disabled={busy !== null}
            />
          ) : null}
          {appleAvailable ? (
            <AppleSignInButton onPress={() => void start('apple', startAppleSignIn)} disabled={busy !== null} />
          ) : null}
          <ErrorText>{error}</ErrorText>
          {config.devSignIn ? (
            <DevSignInForm signIn={(idToken, handle) => session.signIn({ provider: 'google', idToken, nonce: randomString(32) }, handle)} />
          ) : null}
          {!providers && !config.devSignIn ? <Body>No sign-in method is configured for this build yet.</Body> : null}
        </>
      )}
    </Screen>
  );
}
