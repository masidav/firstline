import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';

type Prompt = components['schemas']['Prompt'];

/** A UTC day as YYYY-MM-DD, `offset` days from today. */
export function dayFrom(offset: number, now = new Date()): string {
  const day = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + offset));
  return day.toISOString().slice(0, 10);
}

/** This week's prompt Thursday (UTC; prompts come out on Thursdays), `weeks` weeks on, as YYYY-MM-DD. */
export function thursdayFrom(weeks: number, now = new Date()): string {
  const sinceThursday = (now.getUTCDay() - 4 + 7) % 7;
  return dayFrom(7 * weeks - sinceThursday, now);
}

/**
 * Staff: schedule music prompts (PUT /admin/prompts/{day}). Prompts come out on Thursdays and stay open for the week;
 * weeks without one get the next prompt from the built-in library. A prompt fans have answered can't change.
 */
export function PromptsAdminScreen() {
  const { session } = useSession();
  const [prompts, setPrompts] = useState<Prompt[]>([]);
  const [day, setDay] = useState(thursdayFrom(1));
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState<string>();

  const load = useCallback(async () => {
    try {
      setPrompts(unwrap(await session.api.GET('/admin/prompts')).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function save() {
    setBusy(true);
    setError(undefined);
    setSaved(undefined);
    try {
      const prompt = unwrap(await session.api.PUT('/admin/prompts/{day}', { params: { path: { day } }, body: { text: text.trim() } }));
      setSaved(`Scheduled for ${prompt.day}.`);
      setText('');
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove(promptDay: string) {
    try {
      unwrap(await session.api.DELETE('/admin/prompts/{day}', { params: { path: { day: promptDay } } }));
      await load();
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Weekly prompts</Title>
      <Body muted>
        One prompt a week for every fan, out on Thursdays and open until the next one. Weeks you leave empty get one from
        the built-in list.
      </Body>
      <View style={styles.row}>
        <Button label="This week" kind={day === thursdayFrom(0) ? 'primary' : 'secondary'} onPress={() => setDay(thursdayFrom(0))} />
        <Button label="Next week" kind={day === thursdayFrom(1) ? 'primary' : 'secondary'} onPress={() => setDay(thursdayFrom(1))} />
      </View>
      <Field label="Thursday (YYYY-MM-DD, UTC)" value={day} onChangeText={setDay} maxLength={10} />
      <Field label="Prompt" value={text} onChangeText={setText} maxLength={200} autoCapitalize="sentences" placeholder="What's your best hypothetical band name?" />
      <ErrorText>{error}</ErrorText>
      {saved && <Body>{saved}</Body>}
      <Button label="Schedule" busy={busy} disabled={!text.trim()} onPress={() => void save()} />

      <Text accessibilityRole="header" style={styles.section}>Last week and the coming weeks</Text>
      {prompts.length === 0 && <Body muted>Nothing scheduled.</Body>}
      {prompts.map((prompt) => (
        <View key={prompt.id} style={styles.card}>
          <Text style={styles.muted}>{prompt.day} · {prompt.source === 'staff' ? 'staff' : 'library'} · {prompt.answer_count} answers</Text>
          <Text style={styles.text}>{prompt.text}</Text>
          {prompt.answer_count === 0 && prompt.day >= thursdayFrom(0) && (
            <Button label="Remove" accessibilityLabel={`Remove the prompt for ${prompt.day}`} kind="secondary" onPress={() => void remove(prompt.day)} />
          )}
        </View>
      ))}
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', gap: space.sm },
  section: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
  card: { gap: space.xs, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  text: { color: colors.text, fontSize: 16 },
  muted: { color: colors.muted, fontSize: 13 },
});
