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
import { journalQueue } from './journalQueue';
import { isKnownOffline, isRetryable, type PendingLog } from './pendingLogs';

/**
 * Log an album (POST /logs) or edit an existing log (PATCH /logs/{id}). A new log sends one Idempotency-Key for
 * all its retries, so a flaky connection can't log the album twice. Whether it is a relisten is the server's call.
 *
 * Without a connection the entry is kept on the device instead (`journalQueue`) and sent later; `onQueued` says
 * so. With `pending`, the form edits such a waiting entry in place.
 */
export function LogForm({
  albumId,
  album,
  existing,
  pending,
  onSaved,
  onQueued,
  onCancel,
}: {
  albumId: string;
  /** What to show for the entry while it waits to sync. */
  album: PendingLog['album'];
  existing?: AlbumLog;
  pending?: PendingLog;
  onSaved: (result: LogResult) => void;
  onQueued: (entry: PendingLog) => void;
  onCancel?: () => void;
}) {
  const { session, state } = useSession();
  const userId = state.status === 'signedIn' ? state.me.id : null;
  const initial = pending?.body ?? existing;
  const [rating, setRating] = useState<number | null>(initial?.rating ?? null);
  const [review, setReview] = useState(initial?.review ?? '');
  const [listenedOn, setListenedOn] = useState(initial?.listened_on ?? todayLocal());
  const [visibility, setVisibility] = useState<Visibility>(initial?.visibility ?? 'public');
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
    const body = { rating, review: text || null, listened_on: listenedOn, visibility };
    try {
      if (pending) {
        const edited = await journalQueue.edit(pending.localId, body);
        if (edited) onQueued(edited);
        return;
      }
      if (userId && isKnownOffline()) {
        onQueued(await queue(userId));
        return;
      }
      const result = existing
        ? unwrap(
            await session.api.PATCH('/logs/{logId}', {
              params: { path: { logId: existing.id } },
              body: { rating, review: text || null, listened_on: listenedOn, visibility },
            }),
          )
        : unwrap(
            await session.api.POST('/logs', {
              headers: { 'Idempotency-Key': key },
              body: {
                album_id: albumId,
                ...(rating != null ? { rating } : {}),
                ...(text ? { review: text } : {}),
                listened_on: listenedOn,
                visibility,
              },
            }),
          );
      onSaved(result);
    } catch (e) {
      if (userId && isRetryable(e)) {
        // No connection (or the API is down): keep the entry here and send it later.
        onQueued(await queue(userId));
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

  function queue(owner: string) {
    return journalQueue.add({
      userId: owner,
      key,
      kind: existing ? 'update' : 'create',
      ...(existing ? { logId: existing.id } : {}),
      albumId,
      album,
      body: { rating, review: review.trim() || null, listened_on: listenedOn, visibility },
    });
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
      <Button label={existing || pending ? 'Save changes' : 'Save log'} onPress={() => void save()} busy={saving} />
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
