import { useState } from 'react';

import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { Body, Button, ErrorText, Field } from '../../ui/components';

const NAME_PATTERN = /^[A-Za-z0-9_]{3,30}$/;

/**
 * Developer sign-in (API: Auth__DevSignIn, Development only). Signs in as the test account `name`; the first
 * time, the API asks for a handle and the form shows a second step.
 */
export function DevSignInForm({ signIn }: { signIn: (idToken: string, handle?: string) => Promise<unknown> }) {
  const [name, setName] = useState('');
  const [handle, setHandle] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [handleError, setHandleError] = useState<string>();

  const trimmed = name.trim();
  const choosingHandle = handle !== null;

  async function submit() {
    if (!NAME_PATTERN.test(trimmed)) {
      setError('Use 3 to 30 letters, digits, or underscores.');
      return;
    }
    setBusy(true);
    setError(undefined);
    setHandleError(undefined);
    try {
      await signIn(`dev:${trimmed}`, handle ?? undefined);
    } catch (e) {
      if (e instanceof ApiError && !choosingHandle && e.fieldError('handle')) {
        setHandle(trimmed);
      } else if (e instanceof ApiError && (e.code === 'handle_taken' || e.fieldError('handle'))) {
        setHandleError(errorMessage(e));
      } else {
        setError(errorMessage(e));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Body muted>Developer sign-in: pick any test name. The same name signs back in to the same account.</Body>
      <Field
        label="Test account name"
        value={name}
        onChangeText={(value) => {
          setName(value);
          setHandle(null);
        }}
        placeholder="alice"
        editable={!busy}
        onSubmitEditing={() => void submit()}
      />
      {choosingHandle && (
        <>
          <Body>New account. Choose your handle:</Body>
          <Field
            label="Handle"
            value={handle}
            onChangeText={setHandle}
            error={handleError}
            editable={!busy}
            onSubmitEditing={() => void submit()}
          />
        </>
      )}
      <ErrorText>{error}</ErrorText>
      <Button label={choosingHandle ? 'Create account' : 'Sign in'} onPress={() => void submit()} busy={busy} />
    </>
  );
}
