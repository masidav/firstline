import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { kindLabel, statusLabel, type Submission, submissionConcertLine } from './photoUpload';

/** The fan's posts with their review status (route /posts). Verified concert and merch photos can be shared. */
export function MyPostsScreen() {
  const { session } = useSession();
  const [posts, setPosts] = useState<Submission[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setPosts(unwrap(await session.api.GET('/me/submissions', { params: { query: { limit: 50 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function share(post: Submission, shared: boolean) {
    setBusy(post.id);
    setError(undefined);
    try {
      const updated = unwrap(
        await session.api.PUT('/submissions/{submissionId}/share', {
          params: { path: { submissionId: post.id } },
          body: { shared },
        }),
      );
      setPosts((current) => current?.map((p) => (p.id === updated.id ? updated : p)) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function withdraw(post: Submission) {
    setBusy(post.id);
    setError(undefined);
    try {
      unwrap(await session.api.DELETE('/submissions/{submissionId}', { params: { path: { submissionId: post.id } } }));
      setPosts((current) => current?.filter((p) => p.id !== post.id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!posts && !error) return <Loading />;

  return (
    <Screen>
      <Title>Posts</Title>
      <Button label="Post a photo" onPress={() => router.push('/post')} />
      <ErrorText>{error}</ErrorText>
      {posts?.length === 0 && <Body muted>Nothing yet. Post a concert photo, merch, or a ticket to earn points.</Body>}
      {posts?.map((post) => (
        <View key={post.id} style={styles.card}>
          {post.files?.[0] && (
            <Image source={{ uri: post.files[0].url }} style={styles.photo} resizeMode="cover" accessibilityLabel={`${kindLabel(post.kind)} for ${post.artist.name}`} />
          )}
          <Text style={styles.title}>
            {kindLabel(post.kind)} · {post.artist.name}
          </Text>
          {submissionConcertLine(post) ? <Text style={styles.status}>{submissionConcertLine(post)}</Text> : null}
          {post.caption ? <Text style={styles.caption}>{post.caption}</Text> : null}
          <Text style={styles.status}>{statusLabel(post)}</Text>
          {(post.points_awarded ?? []).length > 0 && (
            <Text style={styles.status}>+{(post.points_awarded ?? []).reduce((sum, a) => sum + a.points, 0)} points</Text>
          )}
          {post.can_share && (
            <Button
              label={post.shared_at ? 'Remove from feed' : 'Share to feed'}
              kind={post.shared_at ? 'secondary' : 'primary'}
              busy={busy === post.id}
              onPress={() => void share(post, !post.shared_at)}
            />
          )}
          {post.status !== 'verified' && (
            <Button label="Withdraw" kind="secondary" busy={busy === post.id} onPress={() => void withdraw(post)} />
          )}
        </View>
      ))}
      <Button label="Back to home" kind="secondary" onPress={() => router.replace('/home')} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  caption: { color: colors.text, fontSize: 15 },
  status: { color: colors.muted, fontSize: 14 },
});
