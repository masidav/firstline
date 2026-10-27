import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { ErrorText } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { fanName } from '../follows/follows';

export type TriviaQuestion = components['schemas']['TriviaQuestion'];

/**
 * After answering: who the fan follows got it right, then how all fans did. "Ben and Cat got it right. 60% of fans
 * did." A fan who is first to answer hears so.
 */
export function resultsLine(results: NonNullable<TriviaQuestion['results']>, fanName: (u: { handle?: string | null; display_name?: string | null }) => string): string {
  const names = results.following_right.map(fanName);
  const people =
    names.length === 2 ? `${names[0]} and ${names[1]} got it right.` : names.length === 1 ? `${names[0]} got it right.` : 'Nobody you follow has it right yet.';
  if (results.answered <= 1) return `You're the first fan to answer this one.`;
  return `${people} ${results.percent_right}% of fans did.`;
}

/** "+0.3 points" without trailing zeros. */
export function pointsLabel(points: number): string {
  return `+${Number(points.toFixed(2))} point${points === 1 ? '' : 's'}`;
}

/**
 * Music trivia (GET /me/trivia): one question a week, out on Tuesdays, about one of the fan's artists. It stays until
 * the fan answers it; then its results show until the next Tuesday. Tapping an option answers
 * it (POST /me/trivia/{id}/answer); the card then shows the right answer and the points a right answer earned (a tenth
 * of logging an album). Hidden when the fan has no artist with enough catalog for a question.
 */
export function DailyTriviaCard() {
  const { session } = useSession();
  const [question, setQuestion] = useState<TriviaQuestion | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    try {
      setQuestion(unwrap(await session.api.GET('/me/trivia')).question ?? null);
    } catch {
      setQuestion(null);   // trivia is a bonus; the feed carries on without it
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function answer(choice: number) {
    if (!question || question.answered || busy) return;
    setBusy(true);
    setError(undefined);
    try {
      setQuestion(
        unwrap(await session.api.POST('/me/trivia/{questionId}/answer', { params: { path: { questionId: question.id } }, body: { choice } })),
      );
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  if (!question) return null;
  return (
    <View style={styles.card}>
      <Text style={styles.kicker}>Trivia · {question.artist.name}</Text>
      <Text style={styles.prompt}>{question.prompt}</Text>
      <View style={styles.options}>
        {question.options.map((option, index) => {
          const right = question.answered && index === question.answer_index;
          const wrongPick = question.answered && index === question.chosen_index && !question.correct;
          return (
            <Pressable
              key={`${index}:${option}`}
              accessibilityRole="button"
              accessibilityLabel={`${option}${right ? ', the right answer' : wrongPick ? ', your answer' : ''}`}
              accessibilityState={{ disabled: question.answered || busy }}
              disabled={question.answered || busy}
              onPress={() => void answer(index)}
              style={({ pressed }) => [styles.option, right && styles.right, wrongPick && styles.wrong, pressed && styles.pressed]}
            >
              <Text style={styles.optionText}>{option}</Text>
            </Pressable>
          );
        })}
      </View>
      {question.answered && (
        <Text style={[styles.result, question.correct ? styles.good : styles.muted]}>
          {question.correct
            ? `Right! ${pointsLabel(question.points_earned ?? question.points_for_right)} for ${question.artist.name}. New trivia every Tuesday.`
            : 'Not this time. New trivia every Tuesday.'}
        </Text>
      )}
      {question.answered && question.results && (
        <Text accessibilityRole="summary" style={styles.results}>{resultsLine(question.results, fanName)}</Text>
      )}
      {!question.answered && (
        <Text style={styles.muted}>Answer right for {pointsLabel(question.points_for_right)} toward your {question.artist.name} score.</Text>
      )}
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.accent, backgroundColor: colors.surface },
  kicker: { color: colors.muted, fontSize: 13, fontWeight: '700', textTransform: 'uppercase', letterSpacing: 0.5 },
  prompt: { color: colors.text, fontSize: 17, fontWeight: '700', lineHeight: 23 },
  options: { gap: space.sm },
  option: { minHeight: 44, borderRadius: 10, borderWidth: 1, borderColor: colors.border, paddingHorizontal: space.md, justifyContent: 'center' },
  right: { borderColor: '#3fb950', backgroundColor: '#12301b' },
  wrong: { borderColor: colors.danger, backgroundColor: '#3a1416' },
  pressed: { opacity: 0.7 },
  optionText: { color: colors.text, fontSize: 15 },
  result: { fontSize: 15, fontWeight: '600' },
  good: { color: '#3fb950' },
  muted: { color: colors.muted, fontSize: 14 },
  results: { color: colors.text, fontSize: 15, lineHeight: 21 },
});
