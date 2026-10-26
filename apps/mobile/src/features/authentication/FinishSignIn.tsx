import { useEffect, useRef, useState } from 'react';

import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import type { Credential } from '../../auth/session';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading } from '../../ui/components';
import { signInErrorMessage, suggestHandle } from './providerSignIn';

/**
 * Sends a provider credential to the API. An existing account is signed in at once (the session state changes
 * and the route redirects); a new account is asked for a handle first, then created with the same credential.
 */
export function FinishSignIn({ credential, onCancel }: { credential: Credential; onCancel: () => void }) {
  const { session, state } = useSession();
  const started = useRef(false);
  const [handle, setHandle] = useState<string | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<string>();
  const [handleError, setHandleError] = useState<string>();

  async function submit(chosenHandle?: string) {
    setBusy(true);
    setError(undefined);
    setHandleError(undefined);
    try {
      await session.signIn(credential, chosenHandle);
    } catch (e) {
      if (e instanceof ApiError && chosenHandle === undefined && e.fieldError('handle')) {
        setHandle(suggestHandle(credential.idToken));
      } else if (e instanceof ApiError && (e.code === 'handle_taken' || e.fieldError('handle'))) {
        setHandleError(errorMessage(e));
      } else {
        setError(signInErrorMessage(e));
      }
      setBusy(false);
    }
  }

  useEffect(() => {
    // Wait for the stored session check on start-up, so its "signed out" result can't land after our sign-in.
    if (state.status === 'loading' || started.current) return;
    started.current = true;
    void submit();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.status]);

  if (error) {
    return (
      <>
        <ErrorText>{error}</ErrorText>
        <Button label="Back to sign-in" kind="secondary" onPress={onCancel} />
      </>
    );
  }
  if (handle === null) {
    return <Loading />;
  }
  return (
    <>
      <Body>Welcome! Choose your handle. It's how other fans see you; you can change it later.</Body>
      <Field
        label="Handle"
        value={handle}
        onChangeText={setHandle}
        error={handleError}
        editable={!busy}
        autoFocus
        onSubmitEditing={() => void submit(handle)}
      />
      <Button label="Create account" onPress={() => void submit(handle)} busy={busy} />
      <Button label="Cancel" kind="secondary" onPress={onCancel} disabled={busy} />
    </>
  );
}
