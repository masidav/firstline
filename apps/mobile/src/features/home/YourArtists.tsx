import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';

import { type Artist, type ScoreSummary, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading } from '../../ui/components';
import { ArtistRow } from './ArtistRow';

/**
 * The tracked artists the fan listens to, best percentile first (GET /me/scores), followed by the artists the
 * fan added that are still onboarding (GET /artists?added_by_me=true&status=onboarding): a Spotify sync adds the
 * fan's top artists automatically, so the list fills right after connecting. Scores come from the fan's synced
 * Spotify listening, so a fan without Spotify connected is asked to connect it.
 */
export function YourArtists() {
  const { session, state } = useSession();
  const [scores, setScores] = useState<ScoreSummary[] | null>(null);
  const [onboarding, setOnboarding] = useState<Artist[]>([]);
  const [error, setError] = useState<string>();
  const connected = state.status === 'signedIn' && state.me.spotify?.connected === true;

  const load = useCallback(async () => {
    setError(undefined);
    try {
      const [scored, added] = await Promise.all([
        session.api.GET('/me/scores', { params: { query: { sort: 'percentile', limit: 100 } } }),
        session.api.GET('/artists', { params: { query: { added_by_me: true, status: 'onboarding', sort: 'recent', limit: 100 } } }),
      ]);
      setScores(unwrap(scored).items);
      setOnboarding(unwrap(added).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error) {
    return (
      <>
        <ErrorText>{error}</ErrorText>
        <Button label="Try again" kind="secondary" onPress={() => void load()} />
      </>
    );
  }
  if (!scores) {
    return <Loading />;
  }
  if (scores.length === 0 && onboarding.length === 0) {
    return connected ? (
      <Body muted>
        None of the artists you listen to are here yet. Search above to add them; your scores show up after your next
        Spotify sync.
      </Body>
    ) : (
      <>
        <Body muted>Connect Spotify and we'll find the artists you listen to and score how big a fan you are.</Body>
        <Button label="Connect Spotify" onPress={() => router.push('/spotify')} />
      </>
    );
  }
  return (
    <>
      {scores.map((score) => (
        <ArtistRow
          key={score.artist_id}
          name={score.artist.name}
          imageUrl={score.artist.image_url}
          note={describeScore(score)}
          onPress={() => router.push(`/artist/${score.artist_id}/albums`)}
        />
      ))}
      {scores.some((score) => score.is_estimated) && (
        <Body muted>An estimate means few fans of that artist are here yet; it settles as more join.</Body>
      )}
      {onboarding.map((artist) => (
        <ArtistRow
          key={artist.id}
          name={artist.name}
          imageUrl={artist.image_url}
          note="Setting up · your score comes with the next sync"
          onPress={() => router.push(`/artist/${artist.id}/albums`)}
        />
      ))}
    </>
  );
}

/** "Top 16% of fans · score 60", plus "estimate" while the percentile leans on the artist's baseline. */
export function describeScore(score: ScoreSummary): string {
  const top = Math.max(1, Math.round(100 - score.percentile));
  const parts = [`Top ${top}% of fans`, `score ${Math.round(score.raw_score)}`];
  if (score.is_estimated) parts.push('estimate');
  return parts.join(' · ');
}
