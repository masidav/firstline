import { StyleSheet, View } from 'react-native';

import { Body, Button } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { formatRating } from './journal';
import { journalQueue } from './journalQueue';
import type { PendingLog } from './pendingLogs';

/**
 * A journal entry saved on this device and not yet accepted by the API: what it says, whether it is waiting or
 * was refused (and why), with Retry, Edit, and Discard. Nothing leaves the device until the fan says so.
 */
export function PendingLogCard({ entry, onEdit, showAlbum }: { entry: PendingLog; onEdit?: () => void; showAlbum?: boolean }) {
  const { body } = entry;
  return (
    <View style={styles.card} accessibilityLabel={`Pending log of ${entry.album.title}`}>
      {showAlbum && (
        <Body>
          {[entry.album.title, entry.album.artistName].filter(Boolean).join(' · ')}
        </Body>
      )}
      <Body>
        {[formatRating(body.rating), body.listened_on, entry.kind === 'update' ? 'edit' : null, body.visibility].filter(Boolean).join(' · ')}
      </Body>
      {body.review ? <Body muted>{body.review.length > 160 ? `${body.review.slice(0, 160)}…` : body.review}</Body> : null}
      {entry.failure ? (
        <Body muted>Couldn't sync: {entry.failure}</Body>
      ) : (
        <Body muted>Saved on this device. It will sync when you're back online.</Body>
      )}
      <View style={styles.actions}>
        {entry.failure && (
          <Button label="Retry" accessibilityLabel={`Retry log of ${entry.album.title}`} kind="secondary" onPress={() => void journalQueue.retry()} />
        )}
        {onEdit && <Button label="Edit" accessibilityLabel={`Edit pending log of ${entry.album.title}`} kind="secondary" onPress={onEdit} />}
        <Button
          label="Discard"
          accessibilityLabel={`Discard pending log of ${entry.album.title}`}
          kind="danger"
          onPress={() => void journalQueue.discard(entry.localId)}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: 2, padding: space.md, borderRadius: 12, backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.border },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm, justifyContent: 'flex-end', marginTop: space.xs },
});
