import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { REPORT_REASONS } from '../feed/ReportForm';
import { stars } from '../feed/Feed';
import { kindLabel } from '../submissions/photoUpload';

type ReportedPost = components['schemas']['ReportedPost'];

const reasonLabel = (reason: string) => REPORT_REASONS.find((r) => r.reason === reason)?.label ?? reason;

/** Staff only (route /moderation): reported feed posts, oldest report first, to take down or dismiss. */
export function ModerationScreen() {
  const { session } = useSession();
  const [posts, setPosts] = useState<ReportedPost[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [paused, setPaused] = useState<Record<string, boolean>>({});

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setPosts(unwrap(await session.api.GET('/admin/reports', { params: { query: { limit: 50 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function decide(post: ReportedPost, action: 'take_down' | 'dismiss') {
    setBusy(post.target_id);
    setError(undefined);
    try {
      const note = notes[post.target_id]?.trim();
      unwrap(
        await session.api.POST('/admin/reports/{targetType}/{targetId}/decision', {
          params: { path: { targetType: post.target_type, targetId: post.target_id } },
          body: note ? { action, note } : { action },
        }),
      );
      setPosts((current) => current?.filter((p) => p.target_id !== post.target_id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  // Flag the post's author as a bot: paused for 30 days (no posting or registering); they can ask to be unbanned.
  async function pauseAuthor(post: ReportedPost) {
    const handle = post.author.handle;
    if (!handle) return;
    setBusy(post.target_id);
    setError(undefined);
    try {
      const note = notes[post.target_id]?.trim();
      unwrap(
        await session.api.POST('/admin/users/{handle}/suspension', {
          params: { path: { handle } },
          body: { reason: 'bot', days: 30, ...(note ? { note } : {}) },
        }),
      );
      setPaused((current) => ({ ...current, [handle]: true }));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!posts && !error) return <Loading />;

  return (
    <Screen>
      <Title>Reported posts</Title>
      <ErrorText>{error}</ErrorText>
      {posts?.length === 0 && <Body muted>No open reports.</Body>}
      {posts?.map((post) => {
        const author = post.author.deleted ? 'Deleted user' : `@${post.author.handle}`;
        return (
          <View key={`${post.target_type}:${post.target_id}`} style={styles.card}>
            <Text style={styles.title}>
              {post.photo ? kindLabel(post.photo.kind) : 'Album log'} · {post.artist.name} · {author}
            </Text>
            {post.photo?.files[0] && (
              <Image source={{ uri: post.photo.files[0].url }} style={styles.photo} resizeMode="contain" accessibilityLabel="Reported photo" />
            )}
            {post.photo?.caption ? <Text style={styles.text}>{post.photo.caption}</Text> : null}
            {post.log && (
              <>
                <Text style={styles.text}>
                  {post.log.album_title}
                  {post.log.rating != null ? ` · ${stars(post.log.rating)}` : ''}
                </Text>
                {post.log.review ? <Text style={styles.text}>{post.log.review}</Text> : null}
              </>
            )}
            <Text style={styles.reports}>
              {post.report_count} {post.report_count === 1 ? 'report' : 'reports'}:{' '}
              {post.reasons.map((r) => `${reasonLabel(r.reason)}${r.count > 1 ? ` ×${r.count}` : ''}`).join(', ')}
            </Text>
            {post.details.map((detail, i) => (
              <Text key={i} style={styles.detail}>
                “{detail}”
              </Text>
            ))}
            <Field
              label="Note (staff only, optional)"
              value={notes[post.target_id] ?? ''}
              onChangeText={(text) => setNotes((current) => ({ ...current, [post.target_id]: text }))}
              maxLength={2000}
              autoCapitalize="sentences"
            />
            <Button label="Take down" kind="danger" busy={busy === post.target_id} onPress={() => void decide(post, 'take_down')} />
            <Button label="Dismiss reports" kind="secondary" busy={busy === post.target_id} onPress={() => void decide(post, 'dismiss')} />
            {post.author.handle &&
              (paused[post.author.handle] ? (
                <Text style={styles.detail}>@{post.author.handle} is paused for 30 days.</Text>
              ) : (
                <Button
                  label="Flag author as a bot (pause 30 days)"
                  kind="secondary"
                  busy={busy === post.target_id}
                  onPress={() => void pauseAuthor(post)}
                />
              ))}
          </View>
        );
      })}
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface },
  text: { color: colors.text, fontSize: 15, lineHeight: 21 },
  reports: { color: colors.danger, fontSize: 14, fontWeight: '600' },
  detail: { color: colors.muted, fontSize: 14, fontStyle: 'italic' },
});
