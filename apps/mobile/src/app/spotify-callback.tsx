import { Redirect, router, useLocalSearchParams } from 'expo-router';
import { useEffect, useRef, useState } from 'react';

import { unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { completeConnect } from '../features/spotify/spotifyConnect';
import { takePendingConnect } from '../features/spotify/spotifyRedirect';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../ui/components';

/** Spotify redirects here after the consent page (?code=&state= or ?error=). */
export default function SpotifyCallback() {
  const params = useLocalSearchParams<{ code?: string; state?: string; error?: string }>();
  const { session, state } = useSession();
  const started = useRef(false);
  const [failure, setFailure] = useState<string>();

  useEffect(() => {
    // The session is restored after the page reload; send the code once, as soon as we're signed in.
    if (state.status !== 'signedIn' || started.current) return;
    started.current = true;
    void completeConnect(params, takePendingConnect(), async (body) =>
      unwrap(await session.api.PUT('/me/spotify', { body })),
    ).then((result) => {
      if (result.ok) {
        session.updateMe({ spotify: result.status });
        router.replace('/spotify');
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
      <Title>Spotify not connected</Title>
      <ErrorText>{failure}</ErrorText>
      <Body muted>Nothing was saved.</Body>
      <Button label="Back to Spotify settings" onPress={() => router.replace('/spotify')} />
    </Screen>
  );
}
