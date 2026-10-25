import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field } from '../../ui/components';
import { colors, space } from '../../ui/theme';

export const REPORT_REASONS = [
  { reason: 'spam', label: 'Spam' },
  { reason: 'abuse', label: 'Abuse or harassment' },
  { reason: 'impersonation', label: 'Impersonation' },
  { reason: 'inappropriate', label: 'Inappropriate' },
  { reason: 'other', label: 'Something else' },
] as const;

type Reason = (typeof REPORT_REASONS)[number]['reason'];

/** Report a feed post to staff (POST /reports): a reason, optional details. Staff decide whether to take it down. */
export function ReportForm({ targetType, targetId, onClose }: { targetType: 'photo' | 'log' | 'prompt_answer'; targetId: string; onClose: () => void }) {
  const { session } = useSession();
  const [reason, setReason] = useState<Reason | null>(null);
  const [details, setDetails] = useState('');
  const [sending, setSending] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string>();

  async function send() {
    if (!reason) return;
    setSending(true);
    setError(undefined);
    try {
      unwrap(
        await session.api.POST('/reports', {
          body: { target_type: targetType, target_id: targetId, reason, ...(details.trim() ? { details: details.trim() } : {}) },
        }),
      );
      setSent(true);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSending(false);
    }
  }

  if (sent) {
    return (
      <View style={styles.box}>
        <Body>Thanks. Staff will look at it.</Body>
        <Button label="Close" kind="secondary" onPress={onClose} />
      </View>
    );
  }

  return (
    <View style={styles.box}>
      <Text style={styles.title}>What's wrong with this post?</Text>
      <View style={styles.reasons} accessibilityRole="radiogroup">
        {REPORT_REASONS.map((option) => (
          <Pressable
            key={option.reason}
            accessibilityRole="radio"
            accessibilityLabel={option.label}
            accessibilityState={{ selected: reason === option.reason }}
            onPress={() => setReason(option.reason)}
            style={[styles.reason, reason === option.reason && styles.reasonSelected]}
          >
            <Text style={[styles.reasonText, reason === option.reason && styles.reasonTextSelected]}>{option.label}</Text>
          </Pressable>
        ))}
      </View>
      <Field
        label="Details (optional)"
        value={details}
        onChangeText={setDetails}
        maxLength={1000}
        autoCapitalize="sentences"
      />
      <ErrorText>{error}</ErrorText>
      <Button label="Send report" kind="danger" onPress={() => void send()} busy={sending} disabled={!reason} />
      <Button label="Cancel" kind="secondary" onPress={onClose} disabled={sending} />
    </View>
  );
}

const styles = StyleSheet.create({
  box: { gap: space.sm, paddingTop: space.sm, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border },
  title: { color: colors.text, fontSize: 15, fontWeight: '600' },
  reasons: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  reason: { borderWidth: 1, borderColor: colors.border, borderRadius: 999, paddingVertical: space.xs, paddingHorizontal: space.md },
  reasonSelected: { backgroundColor: colors.danger, borderColor: colors.danger },
  reasonText: { color: colors.text, fontSize: 14 },
  reasonTextSelected: { color: colors.onAccent, fontWeight: '600' },
});
