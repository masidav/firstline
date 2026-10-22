import { router } from 'expo-router';
import { type ReactNode, useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';

export type UserStats = components['schemas']['UserStats'];
type ArtistStat = UserStats['artists'][number];

/** The signed-in fan's numbers in one list (GET /me/stats): artist scores first, then everything behind them. */
export function StatsScreen() {
  const { session } = useSession();
  const [stats, setStats] = useState<UserStats | null>(null);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setStats(unwrap(await session.api.GET('/me/stats')));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!stats && !error) {
    return <Loading />;
  }

  return (
    <Screen>
      <Title>Your stats</Title>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}

      {stats && (
        <>
          <Section title="Your artists" hint="From your Spotify listening, ranked against the artist's other fans here.">
            {stats.artists.length === 0 ? (
              <Body muted>No scores yet. Connect Spotify and add the artists you love; scores appear after a sync.</Body>
            ) : (
              stats.artists.map((artist) => (
                <Stat key={artist.artist.id} label={artist.artist.name} value={describeArtist(artist)} />
              ))
            )}
            {stats.artists.some((artist) => artist.is_estimated) && (
              <Body muted>An estimate means few fans of that artist are here yet; it settles as more join.</Body>
            )}
          </Section>

          <Section title="Listening" hint="Tracked artists only, from your last Spotify sync.">
            <Stat label="Artists" value={stats.listening.artists} />
            <Stat label="Liked songs" value={stats.listening.liked_tracks} />
            <Stat label="Songs in your playlists" value={stats.listening.playlist_tracks} />
            <Stat label="Albums you have songs from" value={stats.listening.distinct_albums} />
            <Stat label="Recent plays (of your last 50)" value={stats.listening.recent_plays} />
          </Section>

          <Section title="Journal" hint="Deleted logs and comments don't count.">
            <Stat label="Albums logged" value={stats.journal.albums_logged} />
            <Stat label="Logs" value={stats.journal.logs} />
            <Stat label="Relistens" value={stats.journal.relistens} />
            <Stat label="Reviews" value={stats.journal.reviews} />
            <Stat label="Ratings" value={stats.journal.ratings} />
            <Stat label="Lists" value={stats.journal.lists} />
            <Stat label="Comments" value={stats.journal.comments} />
          </Section>

          <Section title="Proof of fandom" hint="Verified submissions only.">
            <Stat label="Tickets" value={stats.proof.tickets} />
            <Stat label="Merch" value={stats.proof.merch} />
            <Stat label="Screenshots" value={stats.proof.screenshots} />
          </Section>

          <Section title="Points" hint="Earned from your journal and verified proof; added to your Spotify scores.">
            <Stat label="Points earned" value={formatNumber(stats.ledger_points)} />
          </Section>
        </>
      )}

      <View style={{ height: space.md }} />
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

/** "Top 16% · #3 of 18 fans · score 61 · estimate" */
export function describeArtist(artist: ArtistStat): string {
  const top = Math.max(1, Math.round(100 - artist.percentile));
  const parts = [`Top ${top}%`, `#${artist.rank} of ${artist.fans} ${artist.fans === 1 ? 'fan' : 'fans'}`, `score ${Math.round(artist.raw_score)}`];
  if (artist.is_estimated) parts.push('estimate');
  return parts.join(' · ');
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(2);
}

function Section({ title, hint, children }: { title: string; hint: string; children: ReactNode }) {
  return (
    <View style={styles.section}>
      <Text accessibilityRole="header" style={styles.sectionTitle}>
        {title}
      </Text>
      <Text style={styles.hint}>{hint}</Text>
      {children}
    </View>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <View style={styles.stat} accessible accessibilityLabel={`${label}: ${value}`}>
      <Text style={styles.label} numberOfLines={1}>
        {label}
      </Text>
      <Text style={styles.value}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  section: { gap: space.sm, marginTop: space.md },
  sectionTitle: { color: colors.text, fontSize: 18, fontWeight: '700' },
  hint: { color: colors.muted, fontSize: 14 },
  stat: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    gap: space.md,
    paddingVertical: space.xs,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: colors.border,
  },
  label: { color: colors.text, fontSize: 16, flexShrink: 1 },
  value: { color: colors.text, fontSize: 16, fontWeight: '600' },
});
