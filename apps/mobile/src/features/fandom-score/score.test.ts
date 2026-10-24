import { describeEstimate, describePercentile, describeTenure, type Score, signalRows } from './score';

const ready: Score = {
  artist_id: 'ar-1',
  status: 'ready',
  percentile: 84.2,
  raw_score: 23.4,
  is_estimated: true,
  baseline_weight: 0.985,
  computed_at: '2026-10-24T12:00:00Z',
  breakdown: {
    spotify: {
      liked_tracks: 12,
      playlist_tracks: 0,
      distinct_albums: 2,
      top_artist_rank: 3,
      recent_plays: 0,
      top_artist_rank_short: null,
      top_artist_rank_long: 40,
      top_tracks: 1,
      saved_albums: 1,
      early_saves: 0,
      first_saved_at: null,
      tenure_days: 400,
      playlists_featuring: 0,
      dedicated_playlists: 0,
      followed: true,
      plays_last_quarter: 7,
      streak_weeks: 2,
      tracks_touched: 9,
      deep_cuts: 0,
      catalog_releases: 5,
      catalog_tracks: 60,
      shares: {
        liked: 0.5,
        playlist: 0,
        album_depth: 0.4,
        top_artist: 0.96,
        recent: 0,
        top_artist_short: 0,
        top_artist_long: 0.22,
        top_track: 0.2,
        saved_album: 0.2,
        early_save: 0,
        tenure: 0.55,
        playlist_feature: null,
        dedicated_playlist: 0,
        follow: 1,
        play_history: 0.45,
        play_streak: 0.17,
        catalog_coverage: 0.18,
        deep_cut: 0,
      },
      points: 23.4,
    },
    ledger_points: 0,
    total: 23.4,
  },
};

test('percentile and estimate wording', () => {
  expect(describePercentile(84.2)).toBe('Top 16% of fans');
  expect(describePercentile(99.9)).toBe('Top 1% of fans');
  expect(describeEstimate(ready)).toContain('99% of this percentile');
  expect(describeEstimate({ ...ready, is_estimated: false })).toBeNull();
});

test('tenure reads as days, months, or years', () => {
  expect(describeTenure(0)).toBe('Not saved yet');
  expect(describeTenure(5)).toBe('5 days');
  expect(describeTenure(1)).toBe('1 day');
  expect(describeTenure(95)).toBe('3 months');
  expect(describeTenure(400)).toBe('1 year');
});

test('signal rows carry the count behind each share and skip disabled rules', () => {
  const rows = signalRows(ready);
  const byKey = Object.fromEntries(rows.map((r) => [r.key, r]));
  expect(byKey.top_artist?.detail).toBe('#3');
  expect(byKey.top_artist_short?.detail).toBe('Not ranked');
  expect(byKey.liked?.detail).toBe('12 songs');
  expect(byKey.top_track?.detail).toBe('1 song');
  expect(byKey.album_depth?.detail).toBe('2 of 5');
  expect(byKey.catalog_coverage?.detail).toBe('9 of 60 songs');
  expect(byKey.tenure?.detail).toBe('1 year');
  expect(byKey.follow?.detail).toBe('Yes');
  expect(byKey.play_streak?.detail).toBe('2 weeks');
  // playlist_feature is disabled (null share), so it is not shown.
  expect(byKey.playlist_feature).toBeUndefined();
  expect(rows).toHaveLength(17);
  expect(rows[0]?.group).toBe('Listening');
});

test('no breakdown means no rows', () => {
  expect(signalRows({ ...ready, breakdown: null })).toEqual([]);
  expect(signalRows({ artist_id: 'ar-1', status: 'computing' })).toEqual([]);
});
