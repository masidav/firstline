import { useState } from 'react';
import { Link } from 'react-router-dom';

import { completeGoogleSignIn } from '../auth/google';
import { ErrorText } from '../ui/components';
import { SignInPage } from './SignInPage';

/** Google redirects here with the ID token in the fragment; it is read once and dropped from the address bar. */
export function GoogleCallbackPage() {
  const [result] = useState(() => {
    const outcome = completeGoogleSignIn(window.location.href);
    window.history.replaceState(null, '', window.location.pathname);
    return outcome;
  });
  if ('credential' in result) return <SignInPage initialCredential={result.credential} />;
  return (
    <main className="page narrow">
      <h1>Not signed in</h1>
      <ErrorText>{result.error}</ErrorText>
      <Link to="/sign-in">Back to sign-in</Link>
    </main>
  );
}
