import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Image, Linking, Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Avatar } from '../../ui/Avatar';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { stars } from '../feed/feedText';
import { FollowButton } from '../follows/FollowButton';
import { setFollowing } from '../follows/follows';
import { SongControls } from '../spotify/SongControls';

export type UserPublic = components['schemas']['UserPublic'];
type ProfileStats = components['schemas']['ProfileStats'];
type GrabBag = components['schemas']['GrabBag'];
type AlbumLog = components['schemas']['AlbumLog'];
type AnswerWithPrompt = { answer: components['schemas']['PromptAnswer']; prompt_text: string; day: string };

/** The band's links from a profile (or the signed-in fan): the band page, Instagram, and X (handles are stored bare). */
export function profileBandLinks(user: { is_artist: boolean; band_url?: string | null; instagram_handle?: string | null; x_handle?: string | null }) {
  if (!user.is_artist) return [];
  const links: { label: string; url: string }[] = [];
  if (user.band_url) links.push({ label: 'Band page', url: user.band_url });
  if (user.instagram_handle) links.push({ label: `Instagram @${user.instagram_handle}`, url: `https://instagram.com/${user.instagram_handle}` });
  if (user.x_handle) links.push({ label: `X @${user.x_handle}`, url: `https://x.com/${user.x_handle}` });
  return links;
}

/** "1 review", "2 reviews". */
export function count(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** "Top 8% of fans · #3 of 41", plus "estimate" while the percentile leans on the baseline. */
export function standingLine(stat: ProfileStats['artists'][number]): string {
  const top = Math.max(1, Math.round(100 - stat.percentile));
  return `Top ${top}% of fans · #${stat.rank} of ${stat.fans}${stat.is_estimated ? ' · estimate' : ''}`;
}

/**
 * A fan's page (/fans/{handle}): who they are, follow, and what they share. Everyone sees the basics, public album logs,
 * and public prompt answers; followers (and the fan) also see fandom stats, the grab bag, and followers-only posts
 * (decided 2026-10-20). Stats are followers-only, none public by default (decided 2026-10-25), so strangers see none.
 */
export function FanProfileScreen({ handle }: { handle: string }) {
  const { session } = useSession();
  const [profile, setProfile] = useState<UserPublic | null>(null);
  const [stats, setStats] = useState<ProfileStats | null>(null);
  const [bag, setBag] = useState<GrabBag | null>(null);
  const [logs, setLogs] = useState<AlbumLog[]>([]);
  const [answers, setAnswers] = useState<AnswerWithPrompt[]>([]);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const user = unwrap(await session.api.GET('/users/{handle}', { params: { path: { handle } } }));
      setProfile(user);
      const [logPage, answerPage] = await Promise.all([
        session.api.GET('/users/{handle}/logs', { params: { path: { handle }, query: { limit: 5 } } }),
        session.api.GET('/users/{handle}/answers', { params: { path: { handle } } }),
      ]);
      setLogs(unwrap(logPage).items);
      setAnswers(unwrap(answerPage).items.slice(0, 5));
      if (user.can_see_follower_content) {
        const [statsPage, bagPage] = await Promise.all([
          session.api.GET('/users/{handle}/stats', { params: { path: { handle } } }),
          session.api.GET('/users/{handle}/grab-bag', { params: { path: { handle } } }),
        ]);
        setStats(unwrap(statsPage));
        setBag(unwrap(bagPage));
      } else {
        setStats(null);
        setBag(null);
      }
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, handle]);

  useEffect(() => {
    void load();
  }, [load]);

  async function toggle(_: string, follow: boolean) {
    setBusy(true);
    setError(undefined);
    try {
      await setFollowing(session.api, handle, follow);
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const back = <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />;
  if (!profile) {
    return (
      <Screen>
        {back}
        <ErrorText>{error}</ErrorText>
        {!error && <Loading />}
      </Screen>
    );
  }

  const name = profile.display_name || `@${profile.handle}`;
  const links = profileBandLinks(profile);
  return (
    <Screen>
      {back}
      <View style={styles.header}>
        <Avatar user={profile} size={64} />
        <View style={styles.grow}>
          <Title>{name}</Title>
          <Text style={styles.muted}>
            @{profile.handle}
            {profile.is_artist ? ' · artist' : ''}
            {profile.follows_me && !profile.is_me ? ' · follows you' : ''}
            {profile.fan_level_name ? ` · ${profile.fan_level_name}` : ''}
          </Text>
        </View>
        {profile.is_me ? (
          <Button label="Edit profile" kind="secondary" onPress={() => router.push('/profile')} />
        ) : (
          <FollowButton handle={profile.handle!} following={profile.followed_by_me} busy={busy} onToggle={(h, f) => void toggle(h, f)} />
        )}
      </View>
      <Text style={styles.counts}>
        {profile.follower_count} {profile.follower_count === 1 ? 'follower' : 'followers'} · {profile.following_count} following ·{' '}
        {profile.log_count} {profile.log_count === 1 ? 'log' : 'logs'}
      </Text>
      {links.map((link) => (
        <Pressable key={link.url} accessibilityRole="link" accessibilityLabel={`Open ${link.label}`} onPress={() => void Linking.openURL(link.url)}>
          <Text style={styles.link}>{link.label}</Text>
        </Pressable>
      ))}
      <ErrorText>{error}</ErrorText>

      {!profile.can_see_follower_content && (
        <Body muted>Follow {name} to see their fandom stats, grab bag, and followers-only posts.</Body>
      )}

      {stats && (
        <Section title="Fandom">
          {stats.artists.length === 0 && <Body muted>No fandom scores yet.</Body>}
          {stats.artists.slice(0, 5).map((stat) => (
            <Pressable key={stat.artist.id} accessibilityRole="button" accessibilityLabel={`Open ${stat.artist.name}`}
              onPress={() => router.push(`/artist/${stat.artist.id}/albums`)} style={styles.row}>
              {stat.artist.image_url ? <Image source={{ uri: stat.artist.image_url }} style={styles.avatar} /> : <View style={styles.avatar} />}
              <View style={styles.grow}>
                <Text style={styles.strong}>{stat.artist.name}</Text>
                <Text style={styles.muted}>{standingLine(stat)}</Text>
              </View>
            </Pressable>
          ))}
          <Text style={styles.muted}>
            {count(stats.journal.albums_logged, 'album logged', 'albums logged')} · {count(stats.journal.reviews, 'review', 'reviews')} ·{' '}
            {count(stats.proof.merch + stats.proof.tickets, 'verified proof', 'verified proofs')} · {count(Math.round(Number(stats.ledger_points)), 'point', 'points')}
          </Text>
        </Section>
      )}

      {bag && bag.items.length > 0 && (
        <Section title="Grab bag">
          {bag.items.map((song) => (
            <View key={song.id} style={styles.row}>
              <Pressable accessibilityRole="link" accessibilityLabel={`Open ${song.title} on Spotify`}
                onPress={() => void Linking.openURL(song.spotify_url)} style={[styles.row, styles.grow]}>
                {song.cover_url ? <Image source={{ uri: song.cover_url }} style={styles.cover} /> : <View style={styles.cover} />}
                <View style={styles.grow}>
                  <Text style={styles.strong} numberOfLines={1}>{song.title}</Text>
                  <Text style={styles.muted} numberOfLines={1}>{song.artist.name}</Text>
                </View>
              </Pressable>
              <SongControls spotifyTrackId={song.spotify_track_id} spotifyUrl={song.spotify_url} title={song.title} />
            </View>
          ))}
        </Section>
      )}

      {logs.length > 0 && (
        <Section title="Recent albums">
          {logs.map((log) => (
            <Pressable key={log.id} accessibilityRole="button" accessibilityLabel={`Open ${log.album.title}`}
              onPress={() => router.push(`/album/${log.album.id}`)} style={styles.row}>
              {log.album.cover_url ? <Image source={{ uri: log.album.cover_url }} style={styles.cover} /> : <View style={styles.cover} />}
              <View style={styles.grow}>
                <Text style={styles.strong} numberOfLines={1}>{log.album.title}</Text>
                <Text style={styles.muted} numberOfLines={1}>
                  {log.album.artist?.name ?? ''}
                  {log.rating != null ? ` · ${stars(log.rating)}` : ''}
                  {log.visibility === 'followers' ? ' · followers only' : ''}
                </Text>
              </View>
            </Pressable>
          ))}
        </Section>
      )}

      {answers.length > 0 && (
        <Section title="Prompt answers">
          {answers.map(({ answer, prompt_text }) => (
            <View key={answer.id} style={styles.answer}>
              <Text style={styles.muted}>{prompt_text}</Text>
              <Text style={styles.text}>“{answer.body}”</Text>
            </View>
          ))}
        </Section>
      )}
    </Screen>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <Text accessibilityRole="header" style={styles.sectionTitle}>{title}</Text>
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  header: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  grow: { flex: 1, gap: 2 },
  counts: { color: colors.text, fontSize: 15 },
  link: { color: colors.accent, fontSize: 15, fontWeight: '600' },
  section: { gap: space.sm, marginTop: space.md },
  sectionTitle: { color: colors.text, fontSize: 18, fontWeight: '700' },
  row: { flexDirection: 'row', alignItems: 'center', gap: space.md },
  avatar: { width: 44, height: 44, borderRadius: 22, backgroundColor: colors.surface },
  cover: { width: 44, height: 44, borderRadius: 6, backgroundColor: colors.surface },
  strong: { color: colors.text, fontSize: 16, fontWeight: '600' },
  muted: { color: colors.muted, fontSize: 14 },
  text: { color: colors.text, fontSize: 16 },
  answer: { gap: 2, paddingVertical: space.xs, borderBottomWidth: 1, borderBottomColor: colors.border },
});
