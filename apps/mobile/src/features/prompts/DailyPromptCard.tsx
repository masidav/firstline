import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Button, ErrorText, Field } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { fanName } from '../follows/follows';

export type PromptToday = components['schemas']['PromptToday'];

const MAX = 280;

/**
 * The current music prompt (GET /prompts/today; out on Thursdays, or daily where the environment runs prompts daily), for example "What's your best hypothetical band name?". The fan answers
 * in a line (PUT /prompts/{id}/answer); answers are public and show on the feed. Once answered, the card
 * shows the answer (edit or delete on the day) and up to three answers from people the fan follows.
 */
export function DailyPromptCard() {
  const { session } = useSession();
  const [today, setToday] = useState<PromptToday | null>(null);
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    try {
      setToday(unwrap(await session.api.GET('/prompts/today')));
    } catch {
      setToday(null);   // the prompt is a bonus; home carries on without it
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!today?.prompt) return null;
  const { prompt, my_answer: mine } = today;

  async function post() {
    setBusy(true);
    setError(undefined);
    try {
      setToday(
        unwrap(
          await session.api.PUT('/prompts/{promptId}/answer', {
            params: { path: { promptId: prompt.id } },
            body: { body: draft.trim(), visibility: 'public' },
          }),
        ),
      );
      setEditing(false);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    setBusy(true);
    setError(undefined);
    try {
      unwrap(await session.api.DELETE('/prompts/{promptId}/answer', { params: { path: { promptId: prompt.id } } }));
      setDraft('');
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const writing = !mine || editing;
  return (
    <View style={styles.card}>
      <Text style={styles.kicker}>Music prompt</Text>
      <Text style={styles.prompt}>{prompt.text}</Text>
      {writing ? (
        <>
          <Field label="Your answer" value={draft} onChangeText={setDraft} maxLength={MAX} multiline autoCapitalize="sentences"
            placeholder="Shared on the feed" />
          <Button label={mine ? 'Save' : 'Post answer'} busy={busy} disabled={draft.trim().length === 0} onPress={() => void post()} />
          {editing && <Button label="Cancel" kind="secondary" onPress={() => setEditing(false)} />}
        </>
      ) : (
        <>
          <Text style={styles.answer}>“{mine.body}”</Text>
          <Text style={styles.muted}>{mine.visibility === 'followers' ? 'Your answer · followers only' : 'Your answer · on the feed'}</Text>
          <View style={styles.row}>
            <Button label="Edit" kind="secondary" onPress={() => {
              setDraft(mine.body);
              setEditing(true);
            }} />
            <Button label="Delete" accessibilityLabel="Delete your answer" kind="secondary" busy={busy} onPress={() => void remove()} />
          </View>
          {today.following_answers.map((answer) => (
            <View key={answer.id} style={styles.other}>
              <Text style={styles.who}>{fanName(answer.user)}</Text>
              <Text style={styles.text}>“{answer.body}”</Text>
            </View>
          ))}
          {prompt.answer_count > 1 && (
            <Button label={`See all ${prompt.answer_count} answers`} kind="secondary" onPress={() => router.push(`/prompts/${prompt.id}`)} />
          )}
        </>
      )}
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: '#e57373', backgroundColor: colors.surface },
  kicker: { color: '#e57373', fontSize: 13, fontWeight: '700', textTransform: 'uppercase', letterSpacing: 0.5 },
  prompt: { color: colors.text, fontSize: 18, fontWeight: '800', lineHeight: 24 },
  row: { flexDirection: 'row', gap: space.sm },
  answer: { color: colors.text, fontSize: 17, fontWeight: '600' },
  other: { gap: 2, paddingTop: space.xs, borderTopWidth: 1, borderTopColor: colors.border },
  who: { color: colors.muted, fontSize: 13, fontWeight: '700' },
  text: { color: colors.text, fontSize: 15 },
  muted: { color: colors.muted, fontSize: 14 },
});
