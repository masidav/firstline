import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { SessionProvider } from '../auth/SessionProvider';
import { JournalSync } from '../features/album-journal/JournalSync';
import { PushSetup } from '../features/notifications/PushSetup';
import { colors } from '../ui/theme';

export default function RootLayout() {
  return (
    <SafeAreaProvider>
      <SessionProvider>
        <JournalSync />
        <PushSetup />
        <StatusBar style="light" />
        <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.background } }} />
      </SessionProvider>
    </SafeAreaProvider>
  );
}
