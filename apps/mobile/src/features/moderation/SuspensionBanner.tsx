import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { formatDate } from '../submissions/photoUpload';

type Suspension = components['schemas']['Suspension'];

/**
 * Shown on home while staff have paused the account (GET /me `suspension`): why, until when, and what it blocks,
 * with one request to be unbanned (POST /me/suspension/appeal) and then its status.
 */
export function SuspensionBanner({ suspension }: { suspension: Suspension }) {
  const { session } = useSession();
  const [asking, setAsking] = useState(false);
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string>();

  async function send() {
    setSending(true);
    setError(undefined);
    try {
      const updated = unwrap(await session.api.POST('/me/suspension/appeal', { body: { message: message.trim() } }));
      session.updateMe({ suspension: updated });
      setAsking(false);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSending(false);
    }
  }

  const appeal = suspension.appeal;
  return (
    <View style={styles.banner} accessibilityRole="alert">
      <Text style={styles.title}>Your account is paused until {formatDate(suspension.ends_at)}</Text>
      <Body>
        {suspension.reason === 'bot' ? 'Staff flagged activity on your account as automated. ' : ''}
        Until then you can't register for tickets, post photos, or log albums.
      </Body>
      {suspension.message ? <Text style={styles.quote}>“{suspension.message}”</Text> : null}

      {!appeal && !asking && <Button label="Ask to be unbanned" kind="secondary" onPress={() => setAsking(true)} />}
      {!appeal && asking && (
        <>
          <Field
            label="Tell staff why"
            value={message}
            onChangeText={setMessage}
            maxLength={1000}
            multiline
            autoCapitalize="sentences"
          />
          <ErrorText>{error}</ErrorText>
          <Button label="Send request" onPress={() => void send()} busy={sending} disabled={!message.trim()} />
          <Button label="Cancel" kind="secondary" onPress={() => setAsking(false)} disabled={sending} />
        </>
      )}
      {appeal?.status === 'pending' && (
        <Body muted>You asked to be unbanned on {formatDate(appeal.submitted_at)}. Staff will review it.</Body>
      )}
      {appeal?.status === 'denied' && (
        <Body muted>Staff reviewed your request and kept the pause{appeal.reply ? `: “${appeal.reply}”` : '.'}</Body>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  banner: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.danger },
  title: { color: colors.text, fontSize: 16, fontWeight: '700' },
  quote: { color: colors.text, fontSize: 15, fontStyle: 'italic' },
});
