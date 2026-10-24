import { router } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { ApiError, type Artist, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { describeEstimate, describePercentile, type Score, type SignalGroup, type SignalRow, signalRows } from './score';

/** While a score is computing, ask again every few seconds, a few times. */
const POLL_MS = 4000;
const MAX_POLLS = 8;

const GROUPS: SignalGroup[] = ['Listening', 'Library', 'Playlists and follows'];

/**
 * The fan's score for one artist (GET /artists/{id}/score): percentile among the artist's fans here, the raw score,
 * and every Spotify signal behind it. Labels estimates, explains what a percentile means, and handles artists that
 * are still onboarding or scores still computing. "Refresh" queues a recompute (POST .../score/refresh).
 */
export function ScoreScreen({ artistId }: { artistId: string }) {
  const { session } = useSession();
  const [artist, setArtist] = useState<Artist | null>(null);
  const [score, setScore] = useState<Score | null>(null);
  const [error, setError] = useState<string>();
  const [gone, setGone] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const polls = useRef(0);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const [artistResult, scoreResult] = await Promise.all([
        session.api.GET('/artists/{artistId}', { params: { path: { artistId } } }),
        session.api.GET('/artists/{artistId}/score', { params: { path: { artistId } } }),
      ]);
      setArtist(unwrap(artistResult));
      setScore(unwrap(scoreResult));
    } catch (e) {
      if (e instanceof ApiError && e.code === 'not_found') setGone(true);
      else setError(errorMessage(e));
    }
  }, [session, artistId]);

  useEffect(() => {
    void load();
  }, [load]);

  // A score still computing usually lands within seconds of a sync.
  useEffect(() => {
    if (score?.status !== 'computing' || polls.current >= MAX_POLLS) return;
    const timer = setTimeout(() => {
      polls.current += 1;
      void load();
    }, POLL_MS);
    return () => clearTimeout(timer);
  }, [score, load]);

  async function refresh() {
    setRefreshing(true);
    setError(undefined);
    try {
      unwrap(await session.api.POST('/artists/{artistId}/score/refresh', { params: { path: { artistId } } }));
      polls.current = 0;
      await load();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setRefreshing(false);
    }
  }

  const back = () => (router.canGoBack() ? router.back() : router.replace('/home'));

  if (gone) {
    return (
      <Screen>
        <Button label="Back" kind="secondary" onPress={back} />
        <Title>Not available</Title>
        <Body muted>This artist isn't on the tracked list any more, so there is no score for them.</Body>
      </Screen>
    );
  }

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={back} />
      <Title>{artist ? `Your ${artist.name} score` : 'Your score'}</Title>
      {error && (
        <>
          <ErrorText>{error}</ErrorText>
          <Button label="Try again" kind="secondary" onPress={() => void load()} />
        </>
      )}
      {!score && !error && <Loading />}
      {score && <ScoreBody score={score} refreshing={refreshing} onRefresh={() => void refresh()} />}
      <View style={styles.links}>
        <Button label="Albums" kind="secondary" onPress={() => router.push(`/artist/${artistId}/albums`)} />
        <Button label="Upcoming shows" kind="secondary" onPress={() => router.push(`/artist/${artistId}/shows`)} />
      </View>
    </Screen>
  );
}

function ScoreBody({ score, refreshing, onRefresh }: { score: Score; refreshing: boolean; onRefresh: () => void }) {
  if (score.status === 'artist_onboarding') {
    return (
      <Body muted>
        This artist is still being set up: their catalog is loading. Your score arrives with your next Spotify sync
        after that, usually within a few minutes.
      </Body>
    );
  }
  if (score.status === 'computing') {
    return (
      <>
        <Body muted>Computing your score… This takes a few seconds after a Spotify sync.</Body>
        <Button label="Refresh" kind="secondary" onPress={onRefresh} busy={refreshing} />
      </>
    );
  }

  const percentile = score.percentile ?? 0;
  const estimate = describeEstimate(score);
  const rows = signalRows(score);
  const ledger = score.breakdown?.ledger_points ?? 0;
  return (
    <>
      <Text style={styles.headline} accessibilityRole="header">
        {describePercentile(percentile)}
      </Text>
      <Text style={styles.subline}>
        Score {Math.round(score.raw_score ?? 0)} · percentile {Math.round(percentile)}
        {score.is_estimated ? ' · estimate' : ''}
      </Text>
      {estimate && <Body muted>{estimate}</Body>}
      <Body muted>
        Percentiles rank you among this artist's fans who use the app, blended with a model of a typical fan while
        there are few, not among every fan in the world.
      </Body>
      {score.computed_at && (
        <Text style={styles.meta}>Computed {new Date(score.computed_at).toLocaleString()}</Text>
      )}
      <Button label="Refresh" kind="secondary" onPress={onRefresh} busy={refreshing} />

      {GROUPS.map((group) => {
        const inGroup = rows.filter((row) => row.group === group);
        if (inGroup.length === 0) return null;
        return (
          <View key={group} style={styles.group}>
            <Text style={styles.groupTitle}>{group}</Text>
            {inGroup.map((row) => (
              <Signal key={row.key} row={row} />
            ))}
          </View>
        );
      })}
      {score.breakdown && (
        <View style={styles.group}>
          <Text style={styles.groupTitle}>Points</Text>
          <Text style={styles.meta}>
            Spotify {Math.round(score.breakdown.spotify?.points ?? 0)} + journal and proof {Math.round(ledger)} = total{' '}
            {Math.round(score.breakdown.total ?? score.raw_score ?? 0)}
          </Text>
        </View>
      )}
    </>
  );
}

function Signal({ row }: { row: SignalRow }) {
  const share = row.share ?? 0;
  return (
    <View style={styles.signal} accessibilityLabel={`${row.label}: ${row.detail}, ${Math.round(share * 100)}% of full marks`}>
      <View style={styles.signalText}>
        <Text style={styles.signalLabel}>{row.label}</Text>
        <Text style={styles.signalDetail}>{row.detail}</Text>
      </View>
      <View style={styles.bar}>
        <View style={[styles.fill, { width: `${Math.round(share * 100)}%` }]} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  headline: { color: colors.text, fontSize: 28, fontWeight: '700' },
  subline: { color: colors.text, fontSize: 16 },
  meta: { color: colors.muted, fontSize: 13 },
  links: { gap: space.sm, marginTop: space.md },
  group: { gap: space.sm, marginTop: space.md },
  groupTitle: { color: colors.text, fontSize: 18, fontWeight: '600' },
  signal: { gap: space.xs },
  signalText: { flexDirection: 'row', justifyContent: 'space-between', gap: space.sm },
  signalLabel: { color: colors.text, fontSize: 15, flexShrink: 1 },
  signalDetail: { color: colors.muted, fontSize: 14 },
  bar: { height: 6, borderRadius: 3, backgroundColor: colors.surface, overflow: 'hidden' },
  fill: { height: 6, backgroundColor: colors.accent },
});
