import { type ReactElement, type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { setFollowing } from '../follows/follows';
import { BouncerDoorCard } from '../grab-bag/BouncerDoorCard';
import { FeedCard } from './FeedCard';
import { type FeedItem, newSeed } from './feedText';

export { stars } from './feedText';
export type { FeedItem } from './feedText';

const PAGE = 20;

/** The bouncer's door sits after this many feed items ("after some scrolling"), or at the end of a shorter feed. */
export const DOOR_AFTER = 6;

type Row = FeedItem | { type: 'bouncer_door'; id: 'door' };

/** The feed with the bouncer's door placed once enough is loaded to know where it goes. */
export function withDoor(items: FeedItem[], morePages: boolean): Row[] {
  // An empty feed shows its empty message instead; a short one waits until the last page is in.
  if (items.length === 0 || (items.length < DOOR_AFTER && morePages)) return items;
  const at = Math.min(DOOR_AFTER, items.length);
  return [...items.slice(0, at), { type: 'bouncer_door', id: 'door' }, ...items.slice(at)];
}

/**
 * The home feed (GET /feed) as one vertical scroll: a random mix of fans' shared photos and album logs, grab-bag
 * songs and stats from the fans you follow, upcoming shows of your artists, and your own fan-ticket registrations.
 * More loads as you scroll (or with "Show more"); pull down or tap Shuffle for a new mix (a new `seed`).
 * `header` renders above the feed; with `active` false (the home search is in use) the feed itself is hidden.
 */
export function Feed({ header, active = true, overlay }: { header?: ReactElement; active?: boolean; overlay?: ReactNode }) {
  const { session, state } = useSession();
  const myHandle = state?.status === 'signedIn' ? state.me.handle : null;
  const [items, setItems] = useState<FeedItem[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [loadingMore, setLoadingMore] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  // One shuffle per visit; Shuffle and pull-to-refresh pick a new one.
  const seed = useRef(newSeed());
  // Follow state by handle, shared by every card of the same fan; the API's answer wins when a page arrives.
  const [following, setFollowingState] = useState<Record<string, boolean>>({});
  const [followBusy, setFollowBusy] = useState<string | null>(null);

  const load = useCallback(
    async (after?: string) => {
      setError(undefined);
      try {
        const page = unwrap(
          await session.api.GET('/feed', { params: { query: { limit: PAGE, cursor: after ?? undefined, seed: seed.current } } }),
        );
        setItems((current) => (after ? [...(current ?? []), ...page.items] : page.items));
        setCursor(page.next_cursor ?? null);
        setFollowingState((current) => {
          const next = { ...current };
          for (const item of page.items) {
            if (item.user?.handle) next[item.user.handle] = item.following;
          }
          return next;
        });
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
    if (!cursor || loadingMore) return;
    setLoadingMore(true);
    await load(cursor);
    setLoadingMore(false);
  }

  async function shuffle() {
    seed.current = newSeed();
    setRefreshing(true);
    await load();
    setRefreshing(false);
  }

  async function toggleFollow(handle: string, follow: boolean) {
    setFollowBusy(handle);
    setFollowingState((current) => ({ ...current, [handle]: follow }));
    try {
      await setFollowing(session.api, handle, follow);
    } catch (e) {
      setFollowingState((current) => ({ ...current, [handle]: !follow }));
      setError(errorMessage(e));
    } finally {
      setFollowBusy(null);
    }
  }

  const data: Row[] = active && items ? withDoor(items, cursor !== null) : [];
  return (
    <SafeAreaView style={styles.safe}>
      <FlatList
        data={data}
        keyExtractor={(item) => `${item.type}:${item.id}`}
        renderItem={({ item }) =>
          item.type === 'bouncer_door' ? (
            <View style={styles.column}>
              <BouncerDoorCard />
            </View>
          ) : (
          <View style={styles.column}>
            <FeedCard
              item={item as FeedItem}
              mine={item.user?.handle != null && item.user.handle === myHandle}
              following={item.user?.handle ? (following[item.user.handle] ?? item.following) : false}
              onFollow={(handle, follow) => {
                if (followBusy === null) void toggleFollow(handle, follow);
              }}
            />
          </View>
          )
        }
        ListHeaderComponent={
          <View style={styles.column}>
            {header}
            {active && (
              <View style={styles.headingRow}>
                <Text accessibilityRole="header" style={styles.heading}>
                  Your feed
                </Text>
                <Pressable accessibilityRole="button" accessibilityLabel="Shuffle the feed" onPress={() => void shuffle()} hitSlop={8}>
                  <Text style={styles.shuffle}>Shuffle</Text>
                </Pressable>
              </View>
            )}
          </View>
        }
        ListEmptyComponent={
          active ? (
            <View style={styles.column}>
              {items === null && !error ? (
                <ActivityIndicator accessibilityLabel="Loading your feed" color={colors.accent} />
              ) : items?.length === 0 ? (
                <Body muted>
                  Nothing yet. Follow some fans, log an album, or post a photo and it shows up here.
                </Body>
              ) : null}
            </View>
          ) : null
        }
        ListFooterComponent={
          active ? (
            <View style={styles.column}>
              <ErrorText>{error}</ErrorText>
              {error && <Button label="Reload feed" kind="secondary" onPress={() => void load(cursor ?? undefined)} />}
              {cursor && !error && <Button label="Show more" kind="secondary" busy={loadingMore} onPress={() => void more()} />}
            </View>
          ) : null
        }
        onEndReached={() => void more()}
        onEndReachedThreshold={0.6}
        refreshing={refreshing}
        onRefresh={() => void shuffle()}
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
      />
      {overlay}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  content: { flexGrow: 1, padding: space.lg, gap: space.md },
  column: { width: '100%', maxWidth: 480, alignSelf: 'center', gap: space.md },
  headingRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: space.sm },
  heading: { color: colors.text, fontSize: 18, fontWeight: '700' },
  shuffle: { color: colors.accent, fontSize: 15, fontWeight: '600' },
});
