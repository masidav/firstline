import { ApiError, type components } from '../../api/client';
import { errorMessage } from '../../api/errors';

export type SpotifyStatus = components['schemas']['SpotifyStatus'];
export type SpotifyConnectBody = components['schemas']['SpotifyConnect'];

/** Same list as SpotifyOptions.RequiredScopes on the server; Spotify must grant all of them. */
export const SPOTIFY_SCOPES = [
  'user-library-read',
  'user-top-read',
  'user-read-recently-played',
  'playlist-read-private',
  'playlist-read-collaborative',
  'user-follow-read',
  'user-read-playback-state',
  'user-modify-playback-state',
  'user-library-modify',
];

/** Scopes a connection must have for every signal to count; older connections may lack newer ones. */
export function missingScopes(status: SpotifyStatus): string[] {
  if (!status.connected || !status.scopes) return [];
  return SPOTIFY_SCOPES.filter((scope) => !status.scopes?.includes(scope));
}

/** Kept between leaving for Spotify and coming back to the callback route. */
export interface PendingConnect {
  state: string;
  verifier: string;
  redirectUri: string;
}

export function authorizeUrl(options: { clientId: string; redirectUri: string; state: string; challenge: string }): string {
  const params = new URLSearchParams({
    client_id: options.clientId,
    response_type: 'code',
    redirect_uri: options.redirectUri,
    code_challenge_method: 'S256',
    code_challenge: options.challenge,
    scope: SPOTIFY_SCOPES.join(' '),
    state: options.state,
  });
  return `https://accounts.spotify.com/authorize?${params.toString()}`;
}

export type ConnectResult = { ok: true; status: SpotifyStatus } | { ok: false; message: string };

/**
 * What starting a connect leads to: on the web the page leaves for Spotify (`redirecting`) and the callback route
 * finishes; in the native app the flow finishes in place, unless the fan closes the browser (`cancelled`).
 */
export type ConnectOutcome = ConnectResult | 'redirecting' | 'cancelled';

/**
 * Handles Spotify's redirect back: checks the state matches the attempt this browser started (so another site
 * can't make us connect *its* Spotify account), then sends the code to the API.
 */
export async function completeConnect(
  params: { code?: string; state?: string; error?: string },
  pending: PendingConnect | null,
  connect: (body: SpotifyConnectBody) => Promise<SpotifyStatus>,
): Promise<ConnectResult> {
  if (params.error) {
    return {
      ok: false,
      message: params.error === 'access_denied' ? "You didn't allow access on Spotify." : `Spotify said: ${params.error}.`,
    };
  }
  if (!pending || !params.code || !params.state || params.state !== pending.state) {
    return { ok: false, message: "This Spotify sign-in expired or didn't start here. Try connecting again." };
  }

  try {
    const status = await connect({ code: params.code, code_verifier: pending.verifier, redirect_uri: pending.redirectUri });
    return { ok: true, status };
  } catch (error) {
    return { ok: false, message: connectErrorMessage(error) };
  }
}

export function connectErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'spotify_in_use') {
      return 'This Spotify account is already connected to another Fandom Score account.';
    }
    if (error.code === 'spotify_unavailable') {
      return "Spotify isn't available right now. Try again in a few minutes.";
    }
    const codeProblem = error.fieldError('code');
    if (codeProblem) {
      return codeProblem;
    }
  }
  return errorMessage(error);
}

/** One line describing the connection for the UI. */
export function describeStatus(status: SpotifyStatus): string {
  if (!status.connected) {
    return 'Not connected.';
  }
  switch (status.sync_state) {
    case 'queued':
    case 'running':
      return 'Syncing your library…';
    case 'reauth_required':
      return 'Spotify stopped accepting this connection. Connect again to keep syncing.';
    case 'failed':
      return status.sync_error === 'spotify_forbidden'
        ? "Spotify refused this account. While the app is in Spotify's development mode, only allowlisted accounts can connect."
        : "The last sync didn't finish. It will try again automatically.";
    default:
      if (!status.last_synced_at) {
        return 'Connected. Waiting for the first sync.';
      }
      return [
        `Last synced ${new Date(status.last_synced_at).toLocaleString()}.`,
        `Found ${status.tracked_artists_found ?? 0} tracked artists in your library.`,
        ...(status.top_artists_added
          ? [`Added ${status.top_artists_added} of your top artists; they count once they're set up.`]
          : []),
      ].join(' ');
  }
}

export function isSyncing(status: SpotifyStatus | null): boolean {
  return status?.sync_state === 'queued' || status?.sync_state === 'running';
}
