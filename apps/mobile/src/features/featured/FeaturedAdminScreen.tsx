import * as ImagePicker from 'expo-image-picker';
import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type Artist, type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import type { FeaturedArtist } from './FeaturedArtistCard';

type Api = ReturnType<typeof useSession>['session']['api'];
export type UploadIntent = components['schemas']['UploadIntent'];

/** The Monday (YYYY-MM-DD, UTC) of the week `weeksAhead` from this one. */
export function mondayOf(weeksAhead = 0, now = new Date()): string {
  const day = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  day.setUTCDate(day.getUTCDate() - ((day.getUTCDay() + 6) % 7) + 7 * weeksAhead);
  return day.toISOString().slice(0, 10);
}

/** Uploads a picked MP4 as a featured video: an upload slot from the API, then the bytes to its signed link. */
export async function uploadVideo(api: Api, uri: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const blob = await (await fetchImpl(uri)).blob();
  const intent = unwrap(await api.POST('/uploads', { body: { purpose: 'featured_video', mime_type: 'video/mp4', bytes: blob.size } }));
  const put = await fetchImpl(intent.url, { method: intent.method, headers: intent.headers ?? {}, body: blob });
  if (!put.ok) throw new Error(put.status === 422 ? "That file isn't an MP4 video." : `Upload failed (${put.status}).`);
  return intent.upload_id;
}

/**
 * Staff: pick the featured artist of the week (PUT /admin/featured/{monday}): a tracked artist, an optional headline,
 * a video (a YouTube or TikTok link, or an MP4 upload), and a Spotify song link. Lists recent and scheduled picks.
 */
export function FeaturedAdminScreen() {
  const { session } = useSession();
  const [picks, setPicks] = useState<FeaturedArtist[]>([]);
  const [week, setWeek] = useState(mondayOf(0));
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<Artist[]>([]);
  const [artist, setArtist] = useState<Artist | null>(null);
  const [headline, setHeadline] = useState('');
  const [videoUrl, setVideoUrl] = useState('');
  const [uploadId, setUploadId] = useState<string | null>(null);
  const [song, setSong] = useState('');
  const [busy, setBusy] = useState<'upload' | 'save' | null>(null);
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState<string>();

  const load = useCallback(async () => {
    try {
      setPicks(unwrap(await session.api.GET('/admin/featured')).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function search() {
    if (!query.trim()) return;
    try {
      setFound(unwrap(await session.api.GET('/artists', { params: { query: { q: query.trim(), status: 'all', sort: 'name', limit: 10 } } })).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  async function pickVideo() {
    const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['videos'], quality: 1 });
    const asset = result.canceled ? undefined : result.assets[0];
    if (!asset) return;
    setBusy('upload');
    setError(undefined);
    try {
      setUploadId(await uploadVideo(session.api, asset.uri));
      setVideoUrl('');
    } catch (e) {
      setError(e instanceof Error && !('status' in e) ? e.message : errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function save() {
    if (!artist) return;
    setBusy('save');
    setError(undefined);
    setSaved(undefined);
    try {
      const pick = unwrap(
        await session.api.PUT('/admin/featured/{weekStart}', {
          params: { path: { weekStart: week } },
          body: {
            artist_id: artist.id,
            headline: headline.trim() || null,
            ...(uploadId ? { video_upload_id: uploadId } : { video_url: videoUrl.trim() }),
            spotify_track: song.trim(),
          },
        }),
      );
      setSaved(`Saved: ${pick.artist.name} for the week of ${pick.week_start}.`);
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function remove(weekStart: string) {
    try {
      unwrap(await session.api.DELETE('/admin/featured/{weekStart}', { params: { path: { weekStart } } }));
      await load();
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  const ready = artist && song.trim() && (uploadId || videoUrl.trim());
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Featured artist</Title>
      <Body muted>One artist a week (Monday to Sunday, UTC) at the top of every fan's feed, with a video and a song.</Body>

      <Text style={styles.label}>Week starting</Text>
      <View style={styles.row}>
        <Button label="This week" kind={week === mondayOf(0) ? 'primary' : 'secondary'} onPress={() => setWeek(mondayOf(0))} />
        <Button label="Next week" kind={week === mondayOf(1) ? 'primary' : 'secondary'} onPress={() => setWeek(mondayOf(1))} />
      </View>
      <Field label="Monday (YYYY-MM-DD)" value={week} onChangeText={setWeek} maxLength={10} />

      <Field label="Artist" placeholder="Search tracked artists" value={query} onChangeText={setQuery} onSubmitEditing={() => void search()}
        returnKeyType="search" autoCapitalize="words" />
      {artist && <Body>Picked: {artist.name}</Body>}
      {found.map((a) => (
        <Pressable key={a.id} accessibilityRole="button" accessibilityLabel={`Pick ${a.name}`} onPress={() => { setArtist(a); setFound([]); }}
          style={styles.choice}>
          <Text style={styles.choiceText}>{a.name}</Text>
        </Pressable>
      ))}

      <Field label="Headline (optional)" value={headline} onChangeText={setHeadline} maxLength={200} autoCapitalize="sentences" />
      <Field label="Video link (YouTube or TikTok)" placeholder="https://youtu.be/..." value={videoUrl}
        onChangeText={(text) => { setVideoUrl(text); setUploadId(null); }} keyboardType="url" />
      <Button label={uploadId ? 'Video uploaded (pick another)' : 'Or upload an MP4'} kind="secondary" busy={busy === 'upload'}
        onPress={() => void pickVideo()} />
      <Field label="Spotify song link" placeholder="https://open.spotify.com/track/..." value={song} onChangeText={setSong} keyboardType="url" />

      <ErrorText>{error}</ErrorText>
      {saved && <Body>{saved}</Body>}
      <Button label="Save pick" busy={busy === 'save'} disabled={!ready || busy !== null} onPress={() => void save()} />

      <Text accessibilityRole="header" style={styles.section}>Recent and scheduled</Text>
      {picks.length === 0 && <Body muted>No picks yet.</Body>}
      {picks.map((pick) => (
        <View key={pick.id} style={styles.pick}>
          <Text style={styles.pickTitle}>{pick.week_start}: {pick.artist.name}</Text>
          <Text style={styles.muted}>{pick.video.kind} video · {pick.song.title}</Text>
          <Button label="Remove" accessibilityLabel={`Remove the pick for ${pick.week_start}`} kind="secondary" onPress={() => void remove(pick.week_start)} />
        </View>
      ))}
    </Screen>
  );
}

const styles = StyleSheet.create({
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  row: { flexDirection: 'row', gap: space.sm },
  choice: { padding: space.sm, borderRadius: 8, borderWidth: 1, borderColor: colors.border },
  choiceText: { color: colors.text, fontSize: 15 },
  section: { color: colors.text, fontSize: 18, fontWeight: '700', marginTop: space.md },
  pick: { gap: space.xs, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  pickTitle: { color: colors.text, fontSize: 16, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
});
