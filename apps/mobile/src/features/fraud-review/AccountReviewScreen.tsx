import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { dateLabel, FAN_APPEAL_STATUS, FAN_STATUS, reasonLabel } from './fraudText';

type FanReview = components['schemas']['FanReview'];
type FanFlag = components['schemas']['FanFlag'];

/** Route /account-review: the fan's own account reviews (GET /me/fraud-review), with one appeal per review. Shows the
 * reason category and status only; evidence and staff notes are never sent to the fan. */
export function AccountReviewScreen() {
  const { session } = useSession();
  const [review, setReview] = useState<FanReview | null>(null);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setReview(unwrap(await session.api.GET('/me/fraud-review')));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!review && !error) return <Loading />;

  return (
    <Screen>
      <Title>Account review</Title>
      <ErrorText>{error}</ErrorText>
      {error && !review && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {review && review.flags.length === 0 && <Body muted>Your account isn't under review. Nothing to do here.</Body>}
      {review?.on_hold && (
        <Body>
          Your account is on hold for fan tickets, so registration, the draw, and returned seats will pass you by for now.
          Everything else works as usual. If something looks wrong, you can tell us your side below.
        </Body>
      )}
      {review && review.flags.length > 0 && !review.on_hold && <Body muted>Nothing is on hold. Past reviews are listed below.</Body>}
      {review?.flags.map((flag) => (
        <FlagCard
          key={flag.id}
          flag={flag}
          onChanged={(updated) =>
            setReview((current) => current && { ...current, flags: current.flags.map((f) => (f.id === updated.id ? updated : f)) })
          }
        />
      ))}
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
    </Screen>
  );
}

function FlagCard({ flag, onChanged }: { flag: FanFlag; onChanged: (flag: FanFlag) => void }) {
  const { session } = useSession();
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string>();

  async function appeal() {
    setSending(true);
    setError(undefined);
    try {
      const filed = unwrap(
        await session.api.POST('/me/fraud-review/flags/{flagId}/appeal', {
          params: { path: { flagId: flag.id } },
          body: { message: message.trim() },
        }),
      );
      onChanged({ ...flag, appeal: filed, can_appeal: false });
      setMessage('');
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSending(false);
    }
  }

  return (
    <View style={styles.card}>
      <Text style={styles.title}>About {reasonLabel(flag.reason)}</Text>
      <Text style={styles.meta}>
        Started {dateLabel(flag.raised_at)}
        {flag.decided_at ? ` · Updated ${dateLabel(flag.decided_at)}` : ''}
      </Text>
      <Body>{FAN_STATUS[flag.status]}</Body>
      {flag.appeal && (
        <View style={styles.appeal}>
          <Text style={styles.label}>Your appeal ({dateLabel(flag.appeal.filed_at)})</Text>
          <Body muted>“{flag.appeal.message}”</Body>
          <Body>{FAN_APPEAL_STATUS[flag.appeal.status]}</Body>
          {flag.appeal.response ? (
            <>
              <Text style={styles.label}>Our response</Text>
              <Body>{flag.appeal.response}</Body>
            </>
          ) : null}
        </View>
      )}
      {flag.can_appeal && (
        <View style={styles.appeal}>
          <Body muted>
            If you think we've got this wrong, tell us what's going on. You can appeal once for this review, and a person on our
            team will read it.
          </Body>
          <Field
            label="Your side"
            value={message}
            onChangeText={setMessage}
            maxLength={2000}
            multiline
            autoCapitalize="sentences"
            autoCorrect
          />
          <ErrorText>{error}</ErrorText>
          <Button label="Send appeal" busy={sending} disabled={!message.trim()} onPress={() => void appeal()} />
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  meta: { color: colors.muted, fontSize: 14 },
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  appeal: { gap: space.sm, paddingTop: space.sm, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border },
});
