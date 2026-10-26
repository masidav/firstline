import { createElement, useCallback, useEffect, useState } from 'react';
import { Image, Linking, Platform, Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { colors, space } from '../../ui/theme';
import { PlayButton } from '../spotify/NowPlaying';

export type FeaturedArtist = components['schemas']['FeaturedArtist'];

/** On the web build, the video plays in the card (an iframe for YouTube and TikTok, a video element for uploads). */
function WebPlayer({ video }: { video: FeaturedArtist['video'] }) {
  const frame = { width: '100%', aspectRatio: video.kind === 'tiktok' ? '9 / 16' : '16 / 9', border: 0, borderRadius: 8, maxHeight: 640 };
  return video.kind === 'upload'
    ? createElement('video', { src: video.url, controls: true, playsInline: true, style: { ...frame, background: '#000' } })
    : createElement('iframe', {
        src: video.embed_url ?? video.url,
        title: 'Featured artist video',
        allow: 'autoplay; encrypted-media; picture-in-picture',
        allowFullScreen: true,
        style: frame,
      });
}

/**
 * Featured artist of the week (GET /featured): the staff pick's video and a song on Spotify, at the top of the feed.
 * On phones the video opens in YouTube, TikTok, or the browser; the web build plays it in place.
 */
export function FeaturedArtistCard() {
  const { session } = useSession();
  const [featured, setFeatured] = useState<FeaturedArtist | null>(null);

  const load = useCallback(async () => {
    try {
      setFeatured(unwrap(await session.api.GET('/featured')).featured ?? null);
    } catch {
      setFeatured(null);
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!featured) return null;
  const { artist, video, song } = featured;
  const playsInPlace = Platform.OS === 'web' && (video.kind === 'upload' || video.embed_url);
  const watchLabel = video.kind === 'youtube' ? 'Watch on YouTube' : video.kind === 'tiktok' ? 'Watch on TikTok' : 'Watch the video';
  return (
    <View style={styles.card}>
      <Text style={styles.kicker}>Featured artist of the week</Text>
      <View style={styles.artistRow}>
        {artist.image_url ? <Image source={{ uri: artist.image_url }} style={styles.avatar} accessibilityIgnoresInvertColors /> : <View style={styles.avatar} />}
        <View style={styles.grow}>
          <Text style={styles.name}>{artist.name}</Text>
          {featured.headline ? <Text style={styles.headline}>{featured.headline}</Text> : null}
        </View>
      </View>
      {playsInPlace ? (
        <WebPlayer video={video} />
      ) : (
        <Pressable accessibilityRole="link" accessibilityLabel={`${watchLabel}: ${artist.name}`} onPress={() => void Linking.openURL(video.url)}
          style={styles.videoTile}>
          {video.thumbnail_url ? <Image source={{ uri: video.thumbnail_url }} style={styles.thumbnail} resizeMode="cover" /> : null}
          <View style={styles.play}>
            <View style={styles.triangle} />
          </View>
          <Text style={styles.watch}>{watchLabel}</Text>
        </Pressable>
      )}
      <View style={styles.songLine}>
        <Pressable accessibilityRole="link" accessibilityLabel={`Listen to ${song.title} on Spotify`} onPress={() => void Linking.openURL(song.spotify_url)}
          style={[styles.songRow, styles.grow]}>
          {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} accessibilityIgnoresInvertColors /> : <View style={styles.cover} />}
          <View style={styles.grow}>
            <Text style={styles.song} numberOfLines={1}>{song.title}</Text>
            <Text style={styles.muted} numberOfLines={1}>{song.album_title ? `${song.album_title} · ` : ''}Listen on Spotify</Text>
          </View>
        </Pressable>
        <PlayButton spotifyTrackId={song.spotify_track_id} spotifyUrl={song.spotify_url} title={song.title} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 2, borderColor: '#e2b04a', backgroundColor: colors.surface },
  kicker: { color: '#e2b04a', fontSize: 13, fontWeight: '700', textTransform: 'uppercase', letterSpacing: 0.5 },
  artistRow: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  avatar: { width: 56, height: 56, borderRadius: 28, backgroundColor: colors.background },
  grow: { flex: 1, gap: 2 },
  name: { color: colors.text, fontSize: 20, fontWeight: '800' },
  headline: { color: colors.text, fontSize: 15 },
  videoTile: { width: '100%', aspectRatio: 16 / 9, borderRadius: 8, overflow: 'hidden', backgroundColor: '#000', alignItems: 'center', justifyContent: 'center' },
  thumbnail: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, opacity: 0.75 },
  play: { width: 60, height: 60, borderRadius: 30, backgroundColor: 'rgba(0,0,0,0.6)', alignItems: 'center', justifyContent: 'center' },
  triangle: { width: 0, height: 0, marginLeft: 6, borderTopWidth: 12, borderBottomWidth: 12, borderLeftWidth: 20, borderTopColor: 'transparent', borderBottomColor: 'transparent', borderLeftColor: '#fff' },
  watch: { position: 'absolute', bottom: space.sm, color: '#fff', fontSize: 14, fontWeight: '700' },
  songRow: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  songLine: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  cover: { width: 48, height: 48, borderRadius: 6, backgroundColor: colors.background },
  song: { color: colors.text, fontSize: 16, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
});
