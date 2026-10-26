import { useEffect, useState } from 'react';
import { Linking, StyleSheet, Switch, View } from 'react-native';

import { type Me, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { space } from '../../ui/theme';
import { type PushState, pushSupported, registerForPush } from './push';

type Category = keyof Me['push'];

const CATEGORIES: { key: Category; label: string; staffOnly?: boolean }[] = [
  { key: 'tickets', label: 'Fan tickets: draw results, seats, delivered tickets' },
  { key: 'presales', label: 'Presale reminders' },
  { key: 'staff', label: 'Staff notices', staffOnly: true },
];

/** Profile section: turn push on for this phone, and choose which notifications are pushed (PATCH /me push). */
export function PushSettingsSection({ me }: { me: Me }) {
  const { session } = useSession();
  const [state, setState] = useState<PushState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    // Where things stand on this phone, without asking.
    if (pushSupported()) void registerForPush(session.api, false).then(setState);
  }, [session]);

  async function turnOn() {
    setBusy(true);
    setState(await registerForPush(session.api, true));
    setBusy(false);
  }

  async function setCategory(key: Category, on: boolean) {
    setError(undefined);
    try {
      const updated = unwrap(await session.api.PATCH('/me', { body: { push: { [key]: on } } }));
      session.updateMe({ push: updated.push });
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  if (!pushSupported()) {
    return <Body muted>Push notifications work in the phone app. Notifications here appear under Alerts.</Body>;
  }

  return (
    <View style={styles.section}>
      <Body>Push notifications</Body>
      {state === 'not_configured' && <Body muted>Push isn't set up for this build of the app yet.</Body>}
      {state === 'denied' && (
        <>
          <Body muted>Notifications are off for Fandom Score in your phone's settings.</Body>
          <Button label="Open settings" kind="secondary" onPress={() => void Linking.openSettings()} />
        </>
      )}
      {state === 'off' && <Button label="Turn on push notifications" busy={busy} onPress={() => void turnOn()} />}
      {state === 'registered' &&
        CATEGORIES.filter((c) => !c.staffOnly || me.is_staff).map((c) => (
          <View key={c.key} style={styles.row}>
            <View style={styles.text}>
              <Body muted>{c.label}</Body>
            </View>
            <Switch accessibilityLabel={`Push: ${c.label}`} value={me.push[c.key]} onValueChange={(on) => void setCategory(c.key, on)} />
          </View>
        ))}
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

const styles = StyleSheet.create({
  section: { gap: space.sm },
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' },
  text: { flex: 1, marginRight: space.md },
});
