import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { FollowButton } from './FollowButton';
import { fanName, setFollowing, type UserSummary } from './follows';

/**
 * People: find fans to follow (GET /users?q=), the fans you follow (GET /me/following), and your followers
 * (GET /me/followers), with follow, unfollow, and remove-follower. Following needs no approval; followers see your
 * followers-only logs, grab bag, and top fandom stats on their feed.
 */
export function PeopleScreen() {
  const { session } = useSession();
  const [following, setFollowingList] = useState<UserSummary[] | null>(null);
  const [followers, setFollowers] = useState<UserSummary[] | null>(null);
  const [error, setError] = useState<string>();
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSummary[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const [mine, theirs] = await Promise.all([
        session.api.GET('/me/following', { params: { query: { limit: 100 } } }),
        session.api.GET('/me/followers', { params: { query: { limit: 100 } } }),
      ]);
      setFollowingList(unwrap(mine).items);
      setFollowers(unwrap(theirs).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function search() {
    const text = query.trim();
    if (!text) return;
    setSearching(true);
    setError(undefined);
    try {
      setResults(unwrap(await session.api.GET('/users', { params: { query: { q: text, limit: 25 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSearching(false);
    }
  }

  // Every list shows the same fan the same way, so one change updates them all.
  function mark(handle: string, followedByMe: boolean) {
    const update = (list: UserSummary[] | null) => list && list.map((u) => (u.handle === handle ? { ...u, followed_by_me: followedByMe } : u));
    setResults(update);
    setFollowers(update);
    setFollowingList((list) => {
      const updated = update(list);
      return followedByMe ? updated : updated && updated.filter((u) => u.handle !== handle);
    });
  }

  async function toggle(handle: string, follow: boolean) {
    setBusy(handle);
    setError(undefined);
    try {
      await setFollowing(session.api, handle, follow);
      mark(handle, follow);
      if (follow) await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function removeFollower(handle: string) {
    setBusy(handle);
    setError(undefined);
    try {
      unwrap(await session.api.DELETE('/me/followers/{handle}', { params: { path: { handle } } }));
      setFollowers((list) => list && list.filter((u) => u.handle !== handle));
      setResults((list) => list && list.map((u) => (u.handle === handle ? { ...u, follows_me: false } : u)));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const row = (user: UserSummary, action: 'follow' | 'follower') => (
    <View key={user.handle} style={styles.row}>
      <Pressable accessibilityRole="link" accessibilityLabel={`Open ${fanName(user)}'s page`} onPress={() => router.push(`/fans/${user.handle}`)}
        style={styles.rowText}>
        <Text style={styles.name} numberOfLines={1}>
          {fanName(user)}
        </Text>
        <Text style={styles.muted} numberOfLines={1}>
          @{user.handle}
          {user.follows_me ? ' · follows you' : ''}
        </Text>
      </Pressable>
      <FollowButton handle={user.handle} following={user.followed_by_me} busy={busy === user.handle} onToggle={(h, f) => void toggle(h, f)} />
      {action === 'follower' && (
        <Button
          label="Remove"
          accessibilityLabel={`Remove follower @${user.handle}`}
          kind="secondary"
          busy={busy === user.handle}
          onPress={() => void removeFollower(user.handle)}
        />
      )}
    </View>
  );

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>People</Title>
      <Body muted>
        Follow fans to see their grab bags, followers-only album logs, and top fandom stats on your feed. No approval
        needed; you can remove a follower any time.
      </Body>
      <ErrorText>{error}</ErrorText>

      <Field
        label="Find fans"
        placeholder="Handle or name"
        value={query}
        onChangeText={(text) => {
          setQuery(text);
          setResults(null);
        }}
        onSubmitEditing={() => void search()}
        returnKeyType="search"
        maxLength={60}
      />
      {searching && <Body muted>Searching…</Body>}
      {results?.length === 0 && <Body muted>No fan matches "{query.trim()}".</Body>}
      {results?.map((user) => row(user, 'follow'))}

      {!following || !followers ? (
        !error && <Loading />
      ) : (
        <>
          <Text accessibilityRole="header" style={styles.section}>
            You follow ({following.length})
          </Text>
          {following.length === 0 && <Body muted>Nobody yet. Search above, or tap Follow on a post in your feed.</Body>}
          {following.map((user) => row(user, 'follow'))}

          <Text accessibilityRole="header" style={styles.section}>
            Your followers ({followers.length})
          </Text>
          {followers.length === 0 && <Body muted>No followers yet.</Body>}
          {followers.map((user) => row(user, 'follower'))}
        </>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  rowText: { flex: 1, gap: 2 },
  name: { color: colors.text, fontSize: 16, fontWeight: '600' },
  muted: { color: colors.muted, fontSize: 14 },
  section: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
});
