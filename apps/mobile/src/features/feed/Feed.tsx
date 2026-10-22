import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { colors, space } from '../../ui/theme';

export type FeedItem = components['schemas']['FeedItem'];

const PAGE = 20;

/** The home feed (GET /feed): photos fans shared and new public album logs, everyone's, newest first. */
export function Feed() {
  const { session } = useSession();
  const [items, setItems] = useState<FeedItem[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [loadingMore, setLoadingMore] = useState(false);

  const load = useCallback(
    async (after?: string) => {
      setError(undefined);
      try {
        const page = unwrap(await session.api.GET('/feed', { params: { query: { limit: PAGE, cursor: after } } }));
        setItems((current) => (after ? [...(current ?? []), ...page.items] : page.items));
        setCursor(page.next_cursor);
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session],
  );

  useEffect(() => {
    void load();
  }, [load]);

  async function more() {
    if (!cursor) return;
    setLoadingMore(true);
    await load(cursor);
    setLoadingMore(false);
  }

  return (
    <View style={styles.feed}>
      <Text accessibilityRole="header" style={styles.heading}>
        Latest from fans
      </Text>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {items?.length === 0 && <Body muted>Nothing yet. Log an album or post a photo and it shows up here.</Body>}
      {items?.map((item) => <FeedCard key={`${item.type}:${item.id}`} item={item} />)}
      {cursor && <Button label="Show more" kind="secondary" busy={loadingMore} onPress={() => void more()} />}
    </View>
  );
}

function FeedCard({ item }: { item: FeedItem }) {
  const who = item.user.display_name || `@${item.user.handle ?? 'someone'}`;
  if (item.type === 'photo' && item.photo) {
    const what = item.photo.kind === 'merch' ? 'merch' : 'a concert photo';
    return (
      <View style={styles.card}>
        <Text style={styles.line}>
          <Text style={styles.strong}>{who}</Text> posted {what} · {item.artist.name}
        </Text>
        {item.photo.files[0] && (
          <Image
            source={{ uri: item.photo.files[0].url }}
            style={styles.photo}
            resizeMode="cover"
            accessibilityLabel={`${who}'s ${what} for ${item.artist.name}`}
          />
        )}
        {item.photo.caption ? <Text style={styles.text}>{item.photo.caption}</Text> : null}
      </View>
    );
  }
  if (item.type === 'album_log' && item.log) {
    const log = item.log;
    return (
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${who} logged ${log.album.title} by ${item.artist.name}`}
        onPress={() => router.push(`/album/${log.album.id}`)}
        style={styles.card}
      >
        <View style={styles.logRow}>
          {log.album.cover_url ? <Image source={{ uri: log.album.cover_url }} style={styles.cover} /> : <View style={styles.cover} />}
          <View style={styles.logText}>
            <Text style={styles.line}>
              <Text style={styles.strong}>{who}</Text> {log.is_relisten ? 'relistened to' : 'logged'}
            </Text>
            <Text style={styles.strong} numberOfLines={1}>
              {log.album.title}
            </Text>
            <Text style={styles.muted}>
              {item.artist.name}
              {log.rating != null ? ` · ${stars(log.rating)}` : ''}
            </Text>
          </View>
        </View>
        {log.review ? <Text style={styles.text}>{log.review}</Text> : null}
      </Pressable>
    );
  }
  return null;
}

/** Ratings are half stars: 9 → "★ 4.5". */
export function stars(rating: number): string {
  return `★ ${(rating / 2).toFixed(rating % 2 === 0 ? 0 : 1)}`;
}

const styles = StyleSheet.create({
  feed: { gap: space.md, marginTop: space.md },
  heading: { color: colors.text, fontSize: 18, fontWeight: '700' },
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  line: { color: colors.text, fontSize: 15 },
  strong: { color: colors.text, fontSize: 15, fontWeight: '700' },
  text: { color: colors.text, fontSize: 15, lineHeight: 21 },
  muted: { color: colors.muted, fontSize: 14 },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  logRow: { flexDirection: 'row', gap: space.md, alignItems: 'center' },
  cover: { width: 56, height: 56, borderRadius: 6, backgroundColor: colors.surface },
  logText: { flex: 1, gap: 2 },
});
