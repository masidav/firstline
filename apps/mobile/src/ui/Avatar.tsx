import { useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { colors } from './theme';

/** What every API shape that names a fan carries (`UserRef`, `UserSummary`, `UserPublic`, `Me`). */
export type AvatarUser = { handle?: string | null; display_name?: string | null; avatar_url?: string | null; deleted?: boolean } | null | undefined;

/** The letter on a fan's placeholder: the first of their display name, else of their handle; "?" for a deleted or unknown fan. */
export function initial(user: AvatarUser): string {
  const name = user?.display_name?.trim() || user?.handle?.trim();
  if (!name || user?.deleted) return '?';
  return [...name][0]!.toUpperCase();
}

// A few steady colours, picked by handle, so a fan without a picture keeps the same one everywhere.
const PALETTE = ['#6c5ce7', '#0984e3', '#00b894', '#e17055', '#d63031', '#e84393', '#00cec9', '#b8860b'];

function tone(user: AvatarUser): string {
  if (!user || user.deleted || !user.handle) return colors.border;
  let hash = 0;
  for (const char of user.handle.toLowerCase()) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return PALETTE[hash % PALETTE.length]!;
}

/**
 * A fan's profile picture (`avatar_url`, a square JPEG the API serves without sign-in) as a circle, or their initial
 * on a colour when they have none, or the picture can't be loaded (removed since the list was fetched). Shown next
 * to a fan's name everywhere, and on the Profile tab for the signed-in fan.
 */
export function Avatar({ user, size = 32 }: { user: AvatarUser; size?: number }) {
  const [failed, setFailed] = useState(false);
  const name = user?.display_name || (user?.handle ? `@${user.handle}` : 'Deleted user');
  const round = { width: size, height: size, borderRadius: size / 2 };
  if (user?.avatar_url && !user.deleted && !failed) {
    return (
      <Image
        source={{ uri: user.avatar_url }}
        style={[styles.image, round]}
        accessibilityLabel={`${name}'s picture`}
        accessibilityIgnoresInvertColors
        onError={() => setFailed(true)}
      />
    );
  }
  return (
    <View style={[styles.placeholder, round, { backgroundColor: tone(user) }]} accessibilityLabel={`${name}'s picture`}>
      <Text style={[styles.initial, { fontSize: Math.round(size * 0.45) }]} allowFontScaling={false}>
        {initial(user)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  image: { backgroundColor: colors.surface },
  placeholder: { alignItems: 'center', justifyContent: 'center' },
  initial: { color: colors.onAccent, fontWeight: '700' },
});
