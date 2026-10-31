import { useEffect, useRef, useState } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom';

import { ApiError, errorMessage } from '../api/client';
import { randomString, startGoogleSignIn } from '../auth/google';
import type { Credential } from '../auth/session';
import { useSession } from '../auth/SessionProvider';
import { config } from '../config';
import { Button, ErrorText, Field } from '../ui/components';

/** Sign in with Google (or developer sign-in locally). A first sign-in creates the account and asks for a handle. */
export function SignInPage({ initialCredential }: { initialCredential?: Credential }) {
  const { session, state } = useSession();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const next = params.get('next') ?? '/';
  const [credential, setCredential] = useState<Credential | null>(initialCredential ?? null);
  const [devName, setDevName] = useState('');
  const [handle, setHandle] = useState('');
  const [needsHandle, setNeedsHandle] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function submit(current: Credential, chosenHandle?: string) {
    setBusy(true);
    setError(undefined);
    try {
      await session.signIn(current, chosenHandle);
      navigate(next, { replace: true });
    } catch (e) {
      if (e instanceof ApiError && !chosenHandle && e.fieldError('handle')) {
        setCredential(current);
        setNeedsHandle(true);
      } else {
        setError(e instanceof ApiError && e.status === 401 ? 'That sign-in expired. Try again.' : errorMessage(e));
      }
    } finally {
      setBusy(false);
    }
  }

  // Coming back from Google: finish at once (the ref keeps StrictMode's second effect run from signing in twice).
  const started = useRef(false);
  useEffect(() => {
    if (initialCredential && !started.current) {
      started.current = true;
      void submit(initialCredential);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // After every hook: React needs the same hooks on every render.
  if (state.status === 'signedIn') return <Navigate to={next} replace />;

  return (
    <main className="page narrow">
      <h1>FirstLine for promoters</h1>
      <p className="muted">Run fan allocations for your shows: face-value tickets for the artist's verified top fans.</p>
      {needsHandle && credential ? (
        <form className="card" onSubmit={(e) => { e.preventDefault(); void submit(credential, handle.trim()); }}>
          <p>New account. Choose your handle; your organization's owner adds you by it.</p>
          <Field label="Handle" value={handle} onChange={(e) => setHandle(e.target.value)} autoFocus />
          <ErrorText>{error}</ErrorText>
          <Button type="submit" busy={busy}>Create account</Button>
        </form>
      ) : (
        <div className="card">
          {config.googleWebClientId && <Button onClick={startGoogleSignIn}>Continue with Google</Button>}
          {config.devSignIn && (
            <form onSubmit={(e) => { e.preventDefault(); void submit({ provider: 'google', idToken: `dev:${devName.trim()}`, nonce: randomString(32) }); }} className="grid">
              <Field label="Test account name" value={devName} onChange={(e) => setDevName(e.target.value)} hint="Developer sign-in: the same name signs back in to the same account." />
              <Button type="submit" kind="secondary" busy={busy}>Sign in</Button>
            </form>
          )}
          {!config.googleWebClientId && !config.devSignIn && <p>No sign-in method is configured for this build.</p>}
          <ErrorText>{error}</ErrorText>
        </div>
      )}
    </main>
  );
}
