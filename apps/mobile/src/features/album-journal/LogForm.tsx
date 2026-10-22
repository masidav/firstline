import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { ApiError, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Button, ErrorText, Field } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import {
  type AlbumLog,
  DATE_PATTERN,
  idempotencyKey,
  type LogResult,
  RATINGS,
  ratingLabel,
  todayLocal,
  VISIBILITIES,
  type Visibility,
} from './journal';
import { isNetworkFailure, outbox, type PendingLog } from './outbox';

/**
 * Log an album (POST /logs) or edit an existing log (PATCH /logs/{id}). A new log sends one Idempotency-Key for
 * all its retries, so a flaky connection can't log the album twice. Whether it is a relisten is the server's call.
 * When a new log can't reach the server at all, it goes to the outbox and `onQueued` is called instead.
 */
export function LogForm({
  albumId,
  albumTitle,
  existing,
  onSaved,
  onQueued,
  onCancel,
}: {
  albumId: string;
  albumTitle: string;
  existing?: AlbumLog;
  onSaved: (result: LogResult) => void;
  onQueued?: (entry: PendingLog) => void;
  onCancel?: () => void;
}) {
  const { session } = useSession();
  const [rating, setRating] = useState<number | null>(existing?.rating ?? null);
  const [review, setReview] = useState(existing?.review ?? '');
  const [listenedOn, setListenedOn] = useState(existing?.listened_on ?? todayLocal());
  const [visibility, setVisibility] = useState<Visibility>(existing?.visibility ?? 'public');
  const [saving, setSaving] = useState(false);
  const [errors, setErrors] = useState<{ date?: string; review?: string; form?: string }>({});
  // Fixed for this form's lifetime: a retry after a failure is the same attempt.
  const [key] = useState(() => idempotencyKey());

  async function save() {
    if (!DATE_PATTERN.test(listenedOn)) {
      setErrors({ date: 'Use the form YYYY-MM-DD.' });
      return;
    }
    setSaving(true);
    setErrors({});
    const text = review.trim();
    const createBody = {
      album_id: albumId,
      ...(rating != null ? { rating } : {}),
      ...(text ? { review: text } : {}),
      listened_on: listenedOn,
      visibility,
    };
    try {
      const result = existing
        ? unwrap(
            await session.api.PATCH('/logs/{logId}', {
              params: { path: { logId: existing.id } },
              body: { rating, review: text || null, listened_on: listenedOn, visibility },
            }),
          )
        : unwrap(await session.api.POST('/logs', { headers: { 'Idempotency-Key': key }, body: createBody }));
      onSaved(result);
    } catch (e) {
      if (!existing && onQueued && isNetworkFailure(e)) {
        const entry: PendingLog = { key, albumId, albumTitle, body: createBody, createdAt: new Date().toISOString() };
        outbox().add(entry);
        onQueued(entry);
        return;
      }
      setErrors(
        e instanceof ApiError
          ? { date: e.fieldError('listened_on'), review: e.fieldError('review'), form: e.problem.errors ? undefined : errorMessage(e) }
          : { form: errorMessage(e) },
      );
    } finally {
      setSaving(false);
    }
  }

  return (
    <View style={styles.form}>
      <Text style={styles.label}>Rating</Text>
      <View style={styles.choices}>
        {RATINGS.map((value) => (
          <Pressable
            key={value}
            accessibilityRole="button"
            accessibilityLabel={`Rate ${value / 2} stars`}
            accessibilityState={{ selected: rating === value }}
            onPress={() => setRating(rating === value ? null : value)}
            style={[styles.choice, rating === value && styles.selected]}
          >
            <Text style={[styles.choiceText, rating === value && styles.selectedText]}>{ratingLabel(value)}</Text>
          </Pressable>
        ))}
      </View>
      <Field
        label="Review"
        placeholder="What did you think? 100+ characters earn review points."
        value={review}
        onChangeText={setReview}
        multiline
        autoCapitalize="sentences"
        maxLength={10000}
        error={errors.review}
      />
      <Field label="Listened on" value={listenedOn} onChangeText={setListenedOn} placeholder="YYYY-MM-DD" error={errors.date} />
      <Text style={styles.label}>Who can see it</Text>
      <View style={styles.choices}>
        {VISIBILITIES.map((option) => (
          <Pressable
            key={option.value}
            accessibilityRole="button"
            accessibilityLabel={`Visibility ${option.label}`}
            accessibilityState={{ selected: visibility === option.value }}
            onPress={() => setVisibility(option.value)}
            style={[styles.choice, visibility === option.value && styles.selected]}
          >
            <Text style={[styles.choiceText, visibility === option.value && styles.selectedText]}>{option.label}</Text>
          </Pressable>
        ))}
      </View>
      <ErrorText>{errors.form}</ErrorText>
      <Button label={existing ? 'Save changes' : 'Save log'} onPress={() => void save()} busy={saving} />
      {onCancel && <Button label="Cancel" kind="secondary" onPress={onCancel} disabled={saving} />}
    </View>
  );
}

const styles = StyleSheet.create({
  form: { gap: space.sm, paddingTop: space.sm },
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  choices: { flexDirection: 'row', flexWrap: 'wrap', gap: space.xs },
  choice: { minWidth: 44, minHeight: 40, paddingHorizontal: space.sm, borderRadius: 8, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  selected: { backgroundColor: colors.accent, borderColor: colors.accent },
  choiceText: { color: colors.text, fontSize: 14 },
  selectedText: { color: colors.onAccent, fontWeight: '600' },
});
