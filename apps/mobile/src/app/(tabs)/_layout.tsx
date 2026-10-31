import { Redirect } from 'expo-router';
import { Tabs } from 'expo-router/js-tabs';

import { useSession } from '../../auth/SessionProvider';
import { useUnreadCount } from '../../features/notifications/unread';
import { NowPlayingBar } from '../../features/spotify/NowPlaying';
import { Avatar } from '../../ui/Avatar';
import { Loading } from '../../ui/components';
import { TabBar } from '../../ui/TabBar';
import { colors } from '../../ui/theme';

/**
 * The signed-in app's tabs: Feed (home), Tickets, Journal, People, Profile. The Profile tab shows the fan's picture
 * (or their initial). Alerts live on the Profile page, whose tab turns red while any are unread. Each stays mounted so switching
 * back is instant and keeps its scroll position; the screens reload on focus. The URLs are unchanged (/home, /tickets…).
 */
export default function TabsLayout() {
  const { state } = useSession();
  const unread = useUnreadCount(state.status === 'signedIn');
  if (state.status === 'loading') return <Loading />;
  if (state.status !== 'signedIn') return <Redirect href="/" />;
  // A hub city is required before anything else (decided 2026-10-20).
  const me = state.me;
  if (me.hub_cities.length === 0) return <Redirect href="/hub-cities" />;

  return (
    <Tabs
      tabBar={(props) => <TabBar {...props} above={<NowPlayingBar insetBottom={false} />} />}
      screenOptions={{ headerShown: false, sceneStyle: { backgroundColor: colors.background } }}
    >
      <Tabs.Screen name="home" options={{ title: 'Feed' }} />
      <Tabs.Screen name="tickets" options={{ title: 'Tickets' }} />
      <Tabs.Screen name="journal" options={{ title: 'Journal' }} />
      <Tabs.Screen name="people" options={{ title: 'People' }} />
      <Tabs.Screen
        name="profile"
        options={{
          title: 'Profile',
          tabBarIcon: ({ size }) => <Avatar user={me} size={size} />,
          tabBarBadge: unread > 0 ? unread : undefined,
          tabBarAccessibilityLabel: unread > 0 ? `Profile, ${unread} unread alerts` : 'Profile',
          ...(unread > 0 && { tabBarActiveTintColor: colors.danger, tabBarInactiveTintColor: colors.danger }),
        }}
      />
    </Tabs>
  );
}
