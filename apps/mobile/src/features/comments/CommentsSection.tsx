import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { ApiError, type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { describePoints, idempotencyKey } from '../album-journal/journal';

export type Comment = components['schemas']['Comment'];

/**
 * The comment thread under a log (GET /logs/{id}/comments) with a composer. Replies are posted with the parent's
 * id and shown indented under it. Points are only mentioned when the API reports an award.
 */
export function CommentsSection({ logId }: { logId: string }) {
  const { session, state } = useSession();
  const myHandle = state.status === 'signedIn' ? state.me.handle : undefined;
  const [comments, setComments] = useState<Comment[] | null>(null);
  const [error, setError] = useState<string>();
  const [replyTo, setReplyTo] = useState<Comment | null>(null);
  const [editing, setEditing] = useState<Comment | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string>();
  const [formError, setFormError] = useState<string>();
  const [key, setKey] = useState(() => idempotencyKey());

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const page = unwrap(await session.api.GET('/logs/{logId}/comments', { params: { path: { logId }, query: { limit: 100 } } }));
      setComments(page.items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, logId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function submit() {
    const body = draft.trim();
    if (!body) {
      setFormError('Write something first.');
      return;
    }
    setBusy(true);
    setFormError(undefined);
    setNotice(undefined);
    try {
      if (editing) {
        unwrap(await session.api.PATCH('/comments/{commentId}', { params: { path: { commentId: editing.id } }, body: { body } }));
        setNotice('Comment updated.');
      } else {
        const result = unwrap(
          await session.api.POST('/logs/{logId}/comments', {
            params: { path: { logId } },
            headers: { 'Idempotency-Key': key },
            body: replyTo ? { body, parent_id: replyTo.id } : { body },
          }),
        );
        setNotice(describePoints(result.points_awarded) ?? 'Posted.');
        setKey(idempotencyKey());
      }
      setDraft('');
      setReplyTo(null);
      setEditing(null);
      await load();
    } catch (e) {
      setFormError(e instanceof ApiError ? (e.fieldError('body') ?? e.fieldError('parent_id') ?? errorMessage(e)) : errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove(comment: Comment) {
    setBusy(true);
    setNotice(undefined);
    try {
      unwrap(await session.api.DELETE('/comments/{commentId}', { params: { path: { commentId: comment.id } } }));
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const ordered = comments ? threaded(comments) : [];
  return (
    <View style={styles.section}>
      <Text accessibilityRole="header" style={styles.heading}>
        {comments ? `${comments.filter((c) => !c.deleted).length} comments` : 'Comments'}
      </Text>
      <ErrorText>{error}</ErrorText>
      {comments?.length === 0 && <Body muted>No comments yet. Be the first; thoughtful ones (20+ characters) earn points.</Body>}
      {ordered.map(({ comment, depth }) => (
        <View key={comment.id} style={[styles.comment, depth > 0 && styles.reply]}>
          <Text style={styles.meta}>
            {comment.user.deleted ? 'Deleted user' : `@${comment.user.handle}`}
            {comment.edited_at ? ' · edited' : ''}
          </Text>
          {comment.deleted ? <Body muted>[deleted]</Body> : <Body>{comment.body}</Body>}
          {!comment.deleted && (
            <View style={styles.actions}>
              <Pressable accessibilityRole="button" accessibilityLabel={`Reply to ${comment.user.handle ?? 'comment'}`} onPress={() => { setReplyTo(comment); setEditing(null); }}>
                <Text style={styles.link}>Reply</Text>
              </Pressable>
              {comment.user.handle === myHandle && (
                <>
                  <Pressable accessibilityRole="button" accessibilityLabel="Edit comment" onPress={() => { setEditing(comment); setReplyTo(null); setDraft(comment.body ?? ''); }}>
                    <Text style={styles.link}>Edit</Text>
                  </Pressable>
                  <Pressable accessibilityRole="button" accessibilityLabel="Delete comment" disabled={busy} onPress={() => void remove(comment)}>
                    <Text style={[styles.link, styles.danger]}>Delete</Text>
                  </Pressable>
                </>
              )}
            </View>
          )}
        </View>
      ))}

      {replyTo && <Body muted>Replying to @{replyTo.user.handle}</Body>}
      {editing && <Body muted>Editing your comment</Body>}
      <Field
        label={editing ? 'Your comment' : replyTo ? 'Your reply' : 'Add a comment'}
        value={draft}
        onChangeText={setDraft}
        multiline
        autoCapitalize="sentences"
        maxLength={2000}
        error={formError}
      />
      {notice && <Body>{notice}</Body>}
      <View style={styles.actions}>
        <Button label={editing ? 'Save' : 'Post'} onPress={() => void submit()} busy={busy} />
        {(replyTo || editing) && (
          <Button label="Cancel" kind="secondary" onPress={() => { setReplyTo(null); setEditing(null); setDraft(''); }} disabled={busy} />
        )}
      </View>
    </View>
  );
}

/** Top-level comments in order, each followed by its replies (one level of indentation is enough to read). */
export function threaded(comments: Comment[]): { comment: Comment; depth: number }[] {
  const byParent = new Map<string | null, Comment[]>();
  for (const comment of comments) {
    const parent = comment.parent_id ?? null;
    byParent.set(parent, [...(byParent.get(parent) ?? []), comment]);
  }
  const out: { comment: Comment; depth: number }[] = [];
  const visit = (parent: string | null, depth: number) => {
    for (const comment of byParent.get(parent) ?? []) {
      out.push({ comment, depth });
      visit(comment.id, Math.min(depth + 1, 1));
    }
  };
  visit(null, 0);
  // A reply whose parent is missing from the page still shows, at the end.
  const seen = new Set(out.map((o) => o.comment.id));
  for (const comment of comments) {
    if (!seen.has(comment.id)) out.push({ comment, depth: 1 });
  }
  return out;
}

const styles = StyleSheet.create({
  section: { gap: space.sm, marginTop: space.md },
  heading: { color: colors.text, fontSize: 18, fontWeight: '700' },
  comment: { gap: 2, padding: space.md, borderRadius: 12, backgroundColor: colors.surface },
  reply: { marginLeft: space.lg },
  meta: { color: colors.muted, fontSize: 13 },
  actions: { flexDirection: 'row', gap: space.md, alignItems: 'center' },
  link: { color: colors.text, fontSize: 14, fontWeight: '600', paddingVertical: space.xs },
  danger: { color: colors.danger },
});
