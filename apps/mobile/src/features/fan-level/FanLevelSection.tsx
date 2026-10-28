import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { Body } from '../../ui/components';
import { colors, space } from '../../ui/theme';

type FanLevel = components['schemas']['FanLevel'];

const PART_LABELS: Record<FanLevel['parts'][number]['key'], string> = {
  fandom: 'Fandom scores (your best three)',
  albums_logged: 'Albums logged',
  verified_proof: 'Verified proof',
  trivia: 'Trivia answered right',
  prompts: 'Prompts answered',
};

/** "Fan level 3 of 5". */
export const levelLabel = (level: number, max = 5) => `Fan level ${level} of ${max}`;

/** "Next level at 60%", or that the fan is at the top. */
export function nextLevelLine(fanLevel: Pick<FanLevel, 'activity' | 'next_level_at'>): string {
  if (fanLevel.next_level_at == null) return "You're at the top level.";
  return `${Math.round(fanLevel.activity * 100)}% now · next level at ${Math.round(fanLevel.next_level_at * 100)}%`;
}

/** Profile: the fan's overall level (GET /me/level) and what goes into it, each part counting the same. */
export function FanLevelSection() {
  const { session } = useSession();
  const [fanLevel, setFanLevel] = useState<FanLevel | null>(null);

  useEffect(() => {
    session.api
      .GET('/me/level')
      .then((r) => setFanLevel(unwrap(r)))
      .catch(() => setFanLevel(null));   // a bonus on the profile; it carries on without it
  }, [session]);

  if (!fanLevel) return null;
  return (
    <View style={styles.card}>
      <Text style={styles.title}>{levelLabel(fanLevel.level, fanLevel.max_level)}</Text>
      <Body muted>{nextLevelLine(fanLevel)}</Body>
      {fanLevel.parts.map((part) => (
        <View key={part.key} style={styles.row} accessibilityLabel={`${PART_LABELS[part.key]}: ${Math.round(part.value * 100)}%`}>
          <Text style={styles.label}>{PART_LABELS[part.key]}</Text>
          <View style={styles.track}>
            <View style={[styles.fill, { width: `${Math.round(part.value * 100)}%` }]} />
          </View>
        </View>
      ))}
      <Body muted>Every part counts the same. Your level shows to you and the fans who follow you.</Body>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border, marginTop: space.md },
  title: { color: colors.text, fontSize: 18, fontWeight: '700' },
  row: { gap: space.xs },
  label: { color: colors.muted, fontSize: 14 },
  track: { height: 6, borderRadius: 3, backgroundColor: colors.surface, overflow: 'hidden' },
  fill: { height: 6, backgroundColor: colors.accent },
});
