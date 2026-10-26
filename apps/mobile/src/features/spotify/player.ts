import { ApiError, type components } from '../../api/client';

export type SpotifyPlayer = components['schemas']['SpotifyPlayer'];

/** How often the bar asks what's playing while the app is open; slower when there's nothing to show. */
export const POLL_MS = 15_000;
export const IDLE_POLL_MS = 60_000;
/** Spotify takes a moment to report a change after a command. */
export const AFTER_COMMAND_MS = 800;

export function isActive(player: SpotifyPlayer | null): boolean {
  return player?.status === 'playing' || player?.status === 'paused';
}

/** Failures after which the song is opened in Spotify instead (it plays there with one tap, Premium or not). */
const OPEN_INSTEAD = new Set([
  'spotify_no_device',
  'spotify_premium_required',
  'spotify_not_connected',
  'spotify_reauth_required',
  'spotify_player_refused',
]);

export function opensInSpotify(error: unknown): boolean {
  return !(error instanceof ApiError) || OPEN_INSTEAD.has(error.code ?? '');
}

/** What the bar says when a play, pause, or skip didn't work. */
export function playerMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return "Couldn't reach Spotify, so the song opened there.";
  switch (error.code) {
    case 'spotify_no_device':
      return "Spotify isn't open on your phone or computer, so the song opened there.";
    case 'spotify_premium_required':
      return 'Playing from here needs Spotify Premium, so the song opened in Spotify.';
    case 'spotify_not_connected':
      return 'Connect Spotify in your profile to play songs here.';
    case 'spotify_reauth_required':
      return 'Connect Spotify again in your profile to play songs here.';
    case 'spotify_unavailable':
      return "Spotify isn't responding. Try again in a moment.";
    default:
      return "Spotify didn't accept that. Try it in the Spotify app.";
  }
}

/** The song's position now, counting on from the last answer while it plays. */
export function progressAt(player: SpotifyPlayer | null, msSinceFetch: number): number | null {
  if (!player?.track || player.progress_ms == null) return null;
  const progress = player.status === 'playing' ? player.progress_ms + msSinceFetch : player.progress_ms;
  const duration = player.track.duration_ms;
  return duration ? Math.min(progress, duration) : progress;
}
