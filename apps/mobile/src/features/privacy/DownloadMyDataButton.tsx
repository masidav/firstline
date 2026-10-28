import { useState } from 'react';

import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { downloadMyData } from './downloadMyData';

const done = {
  downloaded: 'Your data was downloaded as a JSON file.',
  shared: undefined,
  unavailable: "This device can't save files from the app. Try the web app.",
} as const;

/** Profile's "Download my data" (GET /me/export, a few per hour). */
export function DownloadMyDataButton() {
  const { session } = useSession();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const [error, setError] = useState<string>();

  async function download() {
    setBusy(true);
    setMessage(undefined);
    setError(undefined);
    try {
      setMessage(done[await downloadMyData(session.api)]);
    } catch (e) {
      setError(
        e instanceof ApiError && e.code === 'rate_limited'
          ? 'You downloaded your data a few times in the last hour. Try again later.'
          : errorMessage(e),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button label="Download my data" kind="secondary" busy={busy} onPress={() => void download()} />
      {message && <Body muted>{message}</Body>}
      <ErrorText>{error}</ErrorText>
    </>
  );
}
