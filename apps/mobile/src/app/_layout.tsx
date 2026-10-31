import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { SessionProvider } from '../auth/SessionProvider';
import { JournalSync } from '../features/album-journal/JournalSync';
import { PushSetup } from '../features/notifications/PushSetup';
import { SpotifyLibraryProvider } from '../features/spotify/Library';
import { NowPlayingBar, NowPlayingProvider } from '../features/spotify/NowPlaying';
import { colors } from '../ui/theme';

export default function RootLayout() {
  return (
    <SafeAreaProvider>
      <SessionProvider>
        <JournalSync />
        <PushSetup />
        <StatusBar style="light" />
        <NowPlayingProvider>
          <SpotifyLibraryProvider>
            {/* The now-playing bar sits under every screen while the fan's Spotify is playing or paused. */}
            <View style={{ flex: 1, backgroundColor: colors.background }}>
              <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.background } }} />
              <NowPlayingBar />
            </View>
          </SpotifyLibraryProvider>
        </NowPlayingProvider>
      </SessionProvider>
    </SafeAreaProvider>
  );
}
