import type { components } from '../../api/client';

export type Score = components['schemas']['Score'];

/** The signals the scoring worker reports (workers/scoring/README.md), as the app presents them. */
export type SignalGroup = 'Listening' | 'Library' | 'Playlists and follows';

export type SignalRow = {
  key: string;
  label: string;
  group: SignalGroup;
  /** 0 to 1, or null when the rule is disabled. */
  share: number | null;
  /** The count behind the share, e.g. "12 songs". */
  detail: string;
};

type Spotify = NonNullable<NonNullable<Score['breakdown']>['spotify']>;

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;
const rank = (r: number | null | undefined) => (r ? `#${r}` : 'Not ranked');

const SIGNALS: {
  key: keyof NonNullable<Spotify['shares']>;
  label: string;
  group: SignalGroup;
  detail: (s: Spotify) => string;
}[] = [
  { key: 'top_artist', label: 'Top artist, last 6 months', group: 'Listening', detail: (s) => rank(s.top_artist_rank) },
  { key: 'top_artist_short', label: 'Top artist, last 4 weeks', group: 'Listening', detail: (s) => rank(s.top_artist_rank_short) },
  { key: 'top_artist_long', label: 'All-time top artist', group: 'Listening', detail: (s) => rank(s.top_artist_rank_long) },
  { key: 'top_track', label: 'Songs in your top tracks', group: 'Listening', detail: (s) => plural(s.top_tracks ?? 0, 'song') },
  { key: 'recent', label: 'Recent plays (of your last 50)', group: 'Listening', detail: (s) => plural(s.recent_plays ?? 0, 'play') },
  { key: 'play_history', label: 'Plays in the last 90 days', group: 'Listening', detail: (s) => plural(s.plays_last_quarter ?? 0, 'play') },
  { key: 'play_streak', label: 'Weekly listening streak', group: 'Listening', detail: (s) => plural(s.streak_weeks ?? 0, 'week') },
  { key: 'liked', label: 'Liked songs', group: 'Library', detail: (s) => plural(s.liked_tracks ?? 0, 'song') },
  { key: 'saved_album', label: 'Saved albums', group: 'Library', detail: (s) => plural(s.saved_albums ?? 0, 'album') },
  { key: 'album_depth', label: 'Albums you have songs from', group: 'Library', detail: (s) => `${s.distinct_albums ?? 0} of ${s.catalog_releases ?? 0}` },
  { key: 'catalog_coverage', label: 'Catalog explored', group: 'Library', detail: (s) => `${s.tracks_touched ?? 0} of ${s.catalog_tracks ?? 0} songs` },
  { key: 'deep_cut', label: 'Deep cuts', group: 'Library', detail: (s) => plural(s.deep_cuts ?? 0, 'song') },
  { key: 'early_save', label: 'Saved in release week', group: 'Library', detail: (s) => plural(s.early_saves ?? 0, 'release') },
  { key: 'tenure', label: 'Time in your library', group: 'Library', detail: (s) => describeTenure(s.tenure_days ?? 0) },
  { key: 'playlist', label: 'Songs in your playlists', group: 'Playlists and follows', detail: (s) => plural(s.playlist_tracks ?? 0, 'song') },
  { key: 'playlist_feature', label: 'Playlists with the artist', group: 'Playlists and follows', detail: (s) => plural(s.playlists_featuring ?? 0, 'playlist') },
  { key: 'dedicated_playlist', label: 'Playlists all about them', group: 'Playlists and follows', detail: (s) => plural(s.dedicated_playlists ?? 0, 'playlist') },
  { key: 'follow', label: 'Following on Spotify', group: 'Playlists and follows', detail: (s) => (s.followed ? 'Yes' : 'No') },
];

export function describeTenure(days: number): string {
  if (days <= 0) return 'Not saved yet';
  if (days < 30) return plural(days, 'day');
  if (days < 365) return plural(Math.floor(days / 30), 'month');
  return plural(Math.floor(days / 365), 'year');
}

/** Every signal the API reported a share for, in display order; disabled rules (null share) are left out. */
export function signalRows(score: Score): SignalRow[] {
  const spotify = score.breakdown?.spotify;
  const shares = spotify?.shares;
  if (!spotify || !shares) return [];
  return SIGNALS.flatMap(({ key, label, group, detail }) => {
    const share = shares[key];
    return share === null || share === undefined ? [] : [{ key, label, group, share, detail: detail(spotify) }];
  });
}

/** "Top 16% of fans" (percentile 84.2) — never "Top 0%". */
export function describePercentile(percentile: number): string {
  return `Top ${Math.max(1, Math.round(100 - percentile))}% of fans`;
}

/** Why the percentile is an estimate: few fans of this artist are here yet. */
export function describeEstimate(score: Score): string | null {
  if (!score.is_estimated) return null;
  const baseline = score.baseline_weight ?? 0;
  const share = Math.round(baseline * 100);
  return `Estimate: few fans of this artist are here yet, so ${share}% of this percentile still comes from a typical-fan model. It settles as more fans join.`;
}
