import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { colors, space } from '../../ui/theme';

export type Reaction = components['schemas']['Reaction'];
export type ReactionCount = components['schemas']['ReactionCount'];
export type ReactionTarget = 'photo' | 'log' | 'prompt_answer';

/** The fixed set, in display order (decided 2026-10-25; custom emoji from Shelby come later). */
export const REACTIONS: { key: Reaction; emoji: string; name: string }[] = [
  { key: 'fire', emoji: '🔥', name: 'fire' },
  { key: 'heart', emoji: '❤️', name: 'heart' },
  { key: 'laugh', emoji: '😂', name: 'laughing' },
  { key: 'rock', emoji: '🤘', name: 'rock on' },
  { key: 'wow', emoji: '😮', name: 'wow' },
  { key: 'clap', emoji: '👏', name: 'clapping' },
];

const byKey = new Map(REACTIONS.map((r) => [r.key, r]));

/**
 * Emoji reactions under a post (PUT/DELETE /reactions/{type}/{id}/{reaction}): the counts so far, tap one to add or
 * take back yours, or "+" to pick another. Reactions replace comments (decided 2026-10-25).
 */
export function ReactionBar({ targetType, targetId, initial }: { targetType: ReactionTarget; targetId: string; initial: ReactionCount[] }) {
  const { session } = useSession();
  const [counts, setCounts] = useState<ReactionCount[]>(initial);
  const [picking, setPicking] = useState(false);
  const [busy, setBusy] = useState(false);

  async function toggle(reaction: Reaction) {
    if (busy) return;
    const mine = counts.find((c) => c.reaction === reaction)?.mine ?? false;
    setBusy(true);
    setPicking(false);
    try {
      const params = { params: { path: { targetType, targetId, reaction } } };
      const result = unwrap(
        mine ? await session.api.DELETE('/reactions/{targetType}/{targetId}/{reaction}', params) : await session.api.PUT('/reactions/{targetType}/{targetId}/{reaction}', params),
      );
      setCounts(result.reactions);
    } catch {
      // A failed reaction changes nothing; the counts stay as they were.
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.bar}>
      {counts.map((count) => {
        const r = byKey.get(count.reaction);
        if (!r) return null;
        return (
          <Pressable
            key={count.reaction}
            accessibilityRole="button"
            accessibilityLabel={`${r.name}, ${count.count}${count.mine ? ', yours: tap to take it back' : ': tap to add yours'}`}
            onPress={() => void toggle(count.reaction)}
            style={[styles.chip, count.mine && styles.mine]}
          >
            <Text style={styles.emoji}>{r.emoji}</Text>
            <Text style={styles.count}>{count.count}</Text>
          </Pressable>
        );
      })}
      {picking ? (
        REACTIONS.filter((r) => !counts.some((c) => c.reaction === r.key && c.mine)).map((r) => (
          <Pressable key={r.key} accessibilityRole="button" accessibilityLabel={`React with ${r.name}`} onPress={() => void toggle(r.key)}
            style={styles.chip}>
            <Text style={styles.emoji}>{r.emoji}</Text>
          </Pressable>
        ))
      ) : (
        <Pressable accessibilityRole="button" accessibilityLabel="Add a reaction" onPress={() => setPicking(true)} style={styles.chip} hitSlop={4}>
          <Text style={styles.add}>＋</Text>
        </Pressable>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: { flexDirection: 'row', flexWrap: 'wrap', gap: space.xs, alignItems: 'center' },
  chip: {
    flexDirection: 'row', alignItems: 'center', gap: 4, minHeight: 32, paddingHorizontal: 10, borderRadius: 16,
    borderWidth: 1, borderColor: colors.border,
  },
  mine: { borderColor: colors.accent, backgroundColor: '#241f45' },
  emoji: { fontSize: 16 },
  count: { color: colors.text, fontSize: 14, fontWeight: '600' },
  add: { color: colors.muted, fontSize: 16, fontWeight: '700' },
});
