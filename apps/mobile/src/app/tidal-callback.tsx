import { Redirect, router, useLocalSearchParams } from 'expo-router';
import { useEffect, useRef, useState } from 'react';

import { unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { completeTidalConnect } from '../features/music/musicConnect';
import { takePendingTidalConnect } from '../features/music/tidalRedirect';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../ui/components';

/** Tidal redirects here after the consent page (?code=&state= or ?error=). The same shape as /spotify-callback. */
export default function TidalCallback() {
  const params = useLocalSearchParams<{ code?: string; state?: string; error?: string }>();
  const { session, state } = useSession();
  const started = useRef(false);
  const [failure, setFailure] = useState<string>();

  useEffect(() => {
    // The session is restored after the page reload; send the code once, as soon as we're signed in.
    if (state.status !== 'signedIn' || started.current) return;
    started.current = true;
    void completeTidalConnect(params, takePendingTidalConnect(), async (body) =>
      unwrap(await session.api.PUT('/me/music/{provider}', { params: { path: { provider: 'tidal' } }, body })),
    ).then((result) => {
      if (result.ok) {
        router.replace('/music');
      } else {
        setFailure(result.message);
      }
    });
  }, [state, params, session]);

  if (state.status === 'signedOut') {
    return <Redirect href="/" />;
  }
  if (!failure) {
    return <Loading />;
  }
  return (
    <Screen>
      <Title>Tidal not connected</Title>
      <ErrorText>{failure}</ErrorText>
      <Body muted>Nothing was saved.</Body>
      <Button label="Back to music services" onPress={() => router.replace('/music')} />
    </Screen>
  );
}
