import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, Pressable, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { AddToList } from '../lists/AddToList';
import { type AlbumDetail, type AlbumLog, describeAlbum, describePoints, formatRating, type LogResult, spotifyAlbumUrl } from './journal';
import { LogForm } from './LogForm';
import { SpotifyAttribution } from './SpotifyAttribution';

/**
 * One album (GET /albums/{id}): cover and details, tracks, the fan's latest log with edit and "log again",
 * and what other fans wrote (GET /albums/{id}/logs).
 */
export function AlbumScreen({ albumId }: { albumId: string }) {
  const { session } = useSession();
  const [album, setAlbum] = useState<AlbumDetail | null>(null);
  const [community, setCommunity] = useState<AlbumLog[]>([]);
  const [error, setError] = useState<string>();
  const [form, setForm] = useState<'closed' | 'new' | 'edit'>('closed');
  const [saved, setSaved] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const [detail, logs] = await Promise.all([
        session.api.GET('/albums/{albumId}', { params: { path: { albumId } } }),
        session.api.GET('/albums/{albumId}/logs', { params: { path: { albumId }, query: { limit: 10, with_review: true } } }),
      ]);
      setAlbum(unwrap(detail));
      setCommunity(unwrap(logs).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, albumId]);

  useEffect(() => {
    void load();
  }, [load]);

  function onSaved(result: LogResult) {
    setForm('closed');
    const points = describePoints(result.points_awarded);
    setSaved(points ?? (result.log.is_relisten ? 'Relisten saved.' : 'Saved.'));
    void load();
  }

  function onQueued() {
    setForm('closed');
    setSaved("Saved on this device. It will be sent when you're back online; see your journal.");
  }

  if (error && !album) {
    return (
      <Screen>
        <Button label="Back" kind="secondary" onPress={back} />
        <ErrorText>{error}</ErrorText>
        <Button label="Try again" kind="secondary" onPress={() => void load()} />
      </Screen>
    );
  }
  if (!album) return <Loading />;

  const mine = album.my_latest_log ?? null;
  const spotifyUrl = spotifyAlbumUrl(album.spotify_album_id);
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={back} />
      <View style={styles.header}>
        {album.cover_url ? (
          <Image source={{ uri: album.cover_url }} style={styles.cover} accessibilityLabel={`${album.title} cover`} accessibilityIgnoresInvertColors />
        ) : (
          <View style={[styles.cover, styles.placeholder]} />
        )}
        <View style={styles.headerText}>
          <Title>{album.title}</Title>
          <Body>{album.artist.name}</Body>
          <Body muted>{describeAlbum(album)}</Body>
          <Body muted>{describeCommunity(album)}</Body>
        </View>
      </View>
      {album.delisted && <Body muted>Spotify no longer lists this release; your logs of it are kept.</Body>}
      <SpotifyAttribution url={spotifyUrl} />
      <ErrorText>{error}</ErrorText>

      <Text accessibilityRole="header" style={styles.section}>
        Your log
      </Text>
      {saved && <Body>{saved}</Body>}
      {mine && form !== 'edit' && (
        <View style={styles.log}>
          <Body>
            {[formatRating(mine.rating), mine.listened_on, mine.is_relisten ? 'relisten' : null, mine.visibility]
              .filter(Boolean)
              .join(' · ')}
          </Body>
          {mine.review ? <Body>{mine.review}</Body> : null}
          <CommentsLink log={mine} />
        </View>
      )}
      {form === 'closed' && !album.delisted && (
        <View style={styles.actions}>
          {mine && <Button label="Edit" kind="secondary" onPress={() => setForm('edit')} />}
          <Button label={mine ? 'Log again' : 'Log this album'} onPress={() => setForm('new')} />
          <AddToList albumId={album.id} albumTitle={album.title} />
        </View>
      )}
      {form === 'new' && (
        <LogForm albumId={album.id} albumTitle={album.title} onSaved={onSaved} onQueued={onQueued} onCancel={() => setForm('closed')} />
      )}
      {form === 'edit' && mine && (
        <LogForm albumId={album.id} albumTitle={album.title} existing={mine} onSaved={onSaved} onCancel={() => setForm('closed')} />
      )}

      {album.tracks && album.tracks.length > 0 && (
        <>
          <Text accessibilityRole="header" style={styles.section}>
            Tracks
          </Text>
          {album.tracks.map((track) => (
            <Body key={track.id}>
              {track.track_number != null ? `${track.track_number}. ` : ''}
              {track.title}
            </Body>
          ))}
        </>
      )}

      {community.length > 0 && (
        <>
          <Text accessibilityRole="header" style={styles.section}>
            From other fans
          </Text>
          {community.map((log) => (
            <View key={log.id} style={styles.log}>
              <Body>
                {[log.user.deleted ? 'Deleted user' : `@${log.user.handle}`, formatRating(log.rating), log.listened_on].filter(Boolean).join(' · ')}
              </Body>
              {log.review ? <Body muted>{log.review}</Body> : null}
              <CommentsLink log={log} />
            </View>
          ))}
        </>
      )}
    </Screen>
  );
}

/** "3 comments" (or "Comment") opening the log's thread. */
function CommentsLink({ log }: { log: AlbumLog }) {
  const count = log.comment_count ?? 0;
  const label = count === 0 ? 'Comment' : count === 1 ? '1 comment' : `${count} comments`;
  return (
    <Pressable accessibilityRole="link" accessibilityLabel={`${label} on this log`} onPress={() => router.push(`/log/${log.id}`)}>
      <Text style={styles.link}>{label}</Text>
    </Pressable>
  );
}

function back() {
  if (router.canGoBack()) router.back();
  else router.replace('/home');
}

function describeCommunity(album: AlbumDetail): string {
  const count = album.community?.log_count ?? 0;
  if (count === 0) return 'Not logged by anyone yet';
  const average = album.community?.average_rating;
  return `${count} ${count === 1 ? 'log' : 'logs'}${average != null ? ` · ${average} ★ average` : ''}`;
}

const styles = StyleSheet.create({
  header: { flexDirection: 'row', gap: space.md, alignItems: 'flex-start' },
  cover: { width: 120, height: 120, borderRadius: 8, backgroundColor: colors.surface },
  placeholder: {},
  headerText: { flex: 1, gap: 2 },
  section: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
  log: { gap: 2, padding: space.md, borderRadius: 12, backgroundColor: colors.surface },
  actions: { gap: space.sm },
  link: { color: colors.text, fontSize: 14, fontWeight: '600', paddingTop: space.xs },
});
