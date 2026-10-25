import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { ReportForm } from '../feed/ReportForm';
import { fanName } from '../follows/follows';

export type PromptAnswer = components['schemas']['PromptAnswer'];

/** Every answer to a prompt the fan can see (GET /prompts/{id}/answers): theirs, people they follow, then everyone. */
export function PromptAnswersScreen({ promptId }: { promptId: string }) {
  const { session } = useSession();
  const [items, setItems] = useState<PromptAnswer[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [reporting, setReporting] = useState<string | null>(null);

  const load = useCallback(
    async (after?: string) => {
      setError(undefined);
      try {
        const page = unwrap(await session.api.GET('/prompts/{promptId}/answers', { params: { path: { promptId }, query: { limit: 50, cursor: after } } }));
        setItems((current) => (after ? [...(current ?? []), ...page.items] : page.items));
        setCursor(page.next_cursor ?? null);
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session, promptId],
  );

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Answers</Title>
      <ErrorText>{error}</ErrorText>
      {!items && !error && <Loading />}
      {items?.length === 0 && <Body muted>No answers yet.</Body>}
      {items?.map((answer) => (
        <View key={answer.id} style={[styles.row, answer.mine && styles.mine]}>
          <Text style={styles.who} onPress={answer.user.handle ? () => router.push(`/fans/${answer.user.handle}`) : undefined}>
            {answer.mine ? 'You' : fanName(answer.user)}
            {answer.following && !answer.mine ? ' · you follow' : ''}
            {answer.visibility === 'followers' ? ' · followers only' : ''}
          </Text>
          <Text style={styles.text}>“{answer.body}”</Text>
          {!answer.mine &&
            (reporting === answer.id ? (
              <ReportForm targetType="prompt_answer" targetId={answer.id} onClose={() => setReporting(null)} />
            ) : (
              <Pressable accessibilityRole="button" accessibilityLabel={`Report ${fanName(answer.user)}'s answer`} onPress={() => setReporting(answer.id)} hitSlop={8}>
                <Text style={styles.report}>Report</Text>
              </Pressable>
            ))}
        </View>
      ))}
      {cursor && <Button label="Show more" kind="secondary" onPress={() => void load(cursor)} />}
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { gap: space.xs, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  mine: { borderColor: colors.accent },
  who: { color: colors.muted, fontSize: 13, fontWeight: '700' },
  text: { color: colors.text, fontSize: 16 },
  report: { color: colors.muted, fontSize: 13, textDecorationLine: 'underline', alignSelf: 'flex-end' },
});
