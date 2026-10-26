import * as Notifications from 'expo-notifications';
import { router } from 'expo-router';
import { useEffect } from 'react';

import { useSession } from '../../auth/SessionProvider';
import { linkFromResponse, pushSupported, registerForPush } from './push';

if (pushSupported()) {
  // A push that arrives while the app is open still shows as a banner.
  Notifications.setNotificationHandler({
    handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: true, shouldSetBadge: true }),
  });
}

/**
 * Mounted once under SessionProvider (phone app only): refreshes this phone's push registration on launch for a
 * signed-in fan who already allowed notifications, and opens the screen a tapped push points to.
 */
export function PushSetup() {
  const { session, state } = useSession();
  const signedIn = state.status === 'signedIn';

  useEffect(() => {
    if (signedIn && pushSupported()) void registerForPush(session.api, false);
  }, [signedIn, session]);

  useEffect(() => {
    if (!pushSupported()) return;
    // A tap that launched the app, then taps while it runs.
    void Notifications.getLastNotificationResponseAsync().then((response) => {
      const link = linkFromResponse(response);
      if (link) router.push(link as never);
    });
    const subscription = Notifications.addNotificationResponseReceivedListener((response) => {
      const link = linkFromResponse(response);
      if (link) router.push(link as never);
    });
    return () => subscription.remove();
  }, []);

  return null;
}
