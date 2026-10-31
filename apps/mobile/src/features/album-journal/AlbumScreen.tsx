import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';

import { unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Avatar } from '../../ui/Avatar';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { type AlbumDetail, type AlbumLog, describeAlbum, describePoints, formatRating, type LogResult, spotifyAlbumUrl } from './journal';
import { usePendingLogs, useSyncedLogs } from './journalQueue';
import { LogForm } from './LogForm';
import type { PendingLog } from './pendingLogs';
import { PendingLogCard } from './PendingLogCard';
import { SpotifyAttribution } from './SpotifyAttribution';
import { LikeButton } from '../spotify/Library';
import { SpotifyEmbed } from '../spotify/SpotifyEmbed';
import { PlayAlbumButton } from '../spotify/NowPlaying';
import { SongControls } from '../spotify/SongControls';

/**
 * One album (GET /albums/{id}): cover and details, tracks, the fan's latest log with edit and "log again",
 * and what other fans wrote (GET /albums/{id}/logs).
 */
export function AlbumScreen({ albumId }: { albumId: string }) {
  const { session } = useSession();
  const [album, setAlbum] = useState<AlbumDetail | null>(null);
  const [community, setCommunity] = useState<AlbumLog[]>([]);
  const [error, setError] = useState<string>();
  const [form, setForm] = useState<'closed' | 'new' | 'edit' | 'pending'>('closed');
  const [editingPending, setEditingPending] = useState<PendingLog | null>(null);
  const [saved, setSaved] = useState<string>();
  const pending = usePendingLogs(albumId);

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

  // A waiting entry for this album reached the API (maybe while this screen was open): show what it earned.
  useSyncedLogs(
    useCallback(
      (entry: PendingLog, result: LogResult) => {
        if (entry.albumId === albumId) onSaved(result);
      },
      // eslint-disable-next-line react-hooks/exhaustive-deps
      [albumId, load],
    ),
  );

  function onQueued() {
    setForm('closed');
    setEditingPending(null);
    setSaved(undefined);
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
  const albumInfo = { title: album.title, artistName: album.artist.name, coverUrl: album.cover_url ?? null };
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
      {album.spotify_album_id && spotifyUrl && !album.delisted && (
        <View style={styles.albumActions}>
          <PlayAlbumButton spotifyAlbumId={album.spotify_album_id} spotifyUrl={spotifyUrl} title={album.title} />
          <LikeButton kind="album" id={album.spotify_album_id} title={album.title} />
        </View>
      )}
      {!album.delisted && <SpotifyEmbed kind="album" id={album.spotify_album_id} title={album.title} />}
      <SpotifyAttribution url={spotifyUrl} />
      <ErrorText>{error}</ErrorText>

      <Text accessibilityRole="header" style={styles.section}>
        Your log
      </Text>
      {saved && <Body>{saved}</Body>}
      {pending.map((entry) =>
        form === 'pending' && editingPending?.localId === entry.localId ? null : (
          <PendingLogCard
            key={entry.localId}
            entry={entry}
            onEdit={() => {
              setEditingPending(entry);
              setForm('pending');
            }}
          />
        ),
      )}
      {mine && form !== 'edit' && (
        <View style={styles.log}>
          <Body>
            {[formatRating(mine.rating), mine.listened_on, mine.is_relisten ? 'relisten' : null, mine.visibility]
              .filter(Boolean)
              .join(' · ')}
          </Body>
          {mine.review ? <Body>{mine.review}</Body> : null}
        </View>
      )}
      {form === 'closed' && !album.delisted && (
        <View style={styles.actions}>
          {mine && <Button label="Edit" kind="secondary" onPress={() => setForm('edit')} />}
          <Button label={mine ? 'Log again' : 'Log this album'} onPress={() => setForm('new')} />
        </View>
      )}
      {form === 'new' && <LogForm albumId={album.id} album={albumInfo} onSaved={onSaved} onQueued={onQueued} onCancel={() => setForm('closed')} />}
      {form === 'edit' && mine && (
        <LogForm albumId={album.id} album={albumInfo} existing={mine} onSaved={onSaved} onQueued={onQueued} onCancel={() => setForm('closed')} />
      )}
      {form === 'pending' && editingPending && (
        <LogForm albumId={album.id} album={albumInfo} pending={editingPending} onSaved={onSaved} onQueued={onQueued} onCancel={onQueued} />
      )}

      {album.tracks && album.tracks.length > 0 && (
        <>
          <Text accessibilityRole="header" style={styles.section}>
            Tracks
          </Text>
          {album.tracks.map((track) => (
            <View key={track.id} style={styles.track}>
              <View style={styles.trackTitle}>
                <Body>
                  {track.track_number != null ? `${track.track_number}. ` : ''}
                  {track.title}
                </Body>
              </View>
              {track.spotify_track_id && !album.delisted && (
                <SongControls
                  spotifyTrackId={track.spotify_track_id}
                  spotifyUrl={`https://open.spotify.com/track/${track.spotify_track_id}`}
                  title={track.title}
                  compact
                />
              )}
            </View>
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
              <View style={styles.logWho}>
                <Avatar user={log.user} size={24} />
                <Body>
                  {[log.user.deleted ? 'Deleted user' : `@${log.user.handle}`, formatRating(log.rating), log.listened_on].filter(Boolean).join(' · ')}
                </Body>
              </View>
              {log.review ? <Body muted>{log.review}</Body> : null}
            </View>
          ))}
        </>
      )}
    </Screen>
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
  logWho: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  actions: { gap: space.sm },
  albumActions: { flexDirection: 'row', alignItems: 'center', gap: space.sm, flexWrap: 'wrap' },
  track: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  trackTitle: { flex: 1 },
});
