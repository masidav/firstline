import { type Api, ApiError, type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';

// Apple Music and Tidal as listening sources (openapi /me/music, docs/adr/0007-music-providers.md). The same
// shape as Spotify: the app never holds a provider token; the API stores them encrypted.

export type MusicStatus = components['schemas']['MusicStatus'];
export type MusicConnectBody = components['schemas']['MusicConnect'];
export type MusicProvider = MusicStatus['provider'];

/** Every provider the screen shows, in display order. */
export const MUSIC_PROVIDERS: readonly MusicProvider[] = ['apple_music', 'tidal'];

export const PROVIDER_NAMES: Record<MusicProvider, string> = { apple_music: 'Apple Music', tidal: 'Tidal' };

/** Same list as the API's Tidal client; Tidal must grant all of them. */
export const TIDAL_SCOPES = ['user.read', 'collection.read', 'playlists.read'];

/** A provider the fan has not connected, for the UI before the API has said anything about it. */
export function notConnected(provider: MusicProvider): MusicStatus {
  return {
    provider,
    connected: false,
    connected_at: null,
    last_synced_at: null,
    sync_state: 'idle',
    sync_error: null,
    tracked_artists_found: 0,
  };
}

/** Kept between leaving for Tidal and coming back to the callback route. */
export interface PendingTidalConnect {
  state: string;
  verifier: string;
  redirectUri: string;
}

export function tidalAuthorizeUrl(options: { clientId: string; redirectUri: string; state: string; challenge: string }): string {
  const params = new URLSearchParams({
    response_type: 'code',
    client_id: options.clientId,
    redirect_uri: options.redirectUri,
    code_challenge_method: 'S256',
    code_challenge: options.challenge,
    scope: TIDAL_SCOPES.join(' '),
    state: options.state,
  });
  return `https://login.tidal.com/authorize?${params.toString()}`;
}

export type ConnectResult = { ok: true; status: MusicStatus } | { ok: false; message: string };

/**
 * What starting a connect leads to: on the web the page leaves for the provider (`redirecting`) and the callback
 * route finishes; in the native app the flow finishes in place, unless the fan backs out (`cancelled`).
 */
export type ConnectOutcome = ConnectResult | 'redirecting' | 'cancelled';

/**
 * Handles Tidal's redirect back: checks the state matches the attempt this browser started (so another site can't
 * make us connect *its* Tidal account), then sends the code to the API (`PUT /me/music/tidal`).
 */
export async function completeTidalConnect(
  params: { code?: string; state?: string; error?: string },
  pending: PendingTidalConnect | null,
  connect: (body: MusicConnectBody) => Promise<MusicStatus>,
): Promise<ConnectResult> {
  if (params.error) {
    return {
      ok: false,
      message: params.error === 'access_denied' ? "You didn't allow access on Tidal." : `Tidal said: ${params.error}.`,
    };
  }
  if (!pending || !params.code || !params.state || params.state !== pending.state) {
    return { ok: false, message: "This Tidal sign-in expired or didn't start here. Try connecting again." };
  }

  try {
    const status = await connect({ code: params.code, code_verifier: pending.verifier, redirect_uri: pending.redirectUri });
    return { ok: true, status };
  } catch (error) {
    return { ok: false, message: connectErrorMessage(error, 'tidal') };
  }
}

/**
 * The Apple Music connect flow around a platform's MusicKit (MusicKit JS on the web, the local MusicKit module on
 * iOS): the developer token from the API (signed with its MusicKit key), then `authorize` for the fan's Music User
 * Token, then that token to the API (`PUT /me/music/apple_music`), which checks and stores it. `authorize` resolves
 * to no token when the fan backed out (`cancelled`), and throws with a message to show when MusicKit itself failed.
 */
export async function connectAppleMusicWith(
  api: Api,
  authorize: (developerToken: string) => Promise<string | null | undefined>,
): Promise<ConnectResult | 'cancelled'> {
  let developerToken: string;
  try {
    developerToken = unwrap(await api.GET('/me/music/apple_music/developer-token')).token;
  } catch (error) {
    return { ok: false, message: connectErrorMessage(error, 'apple_music') };
  }

  let musicUserToken: string | null | undefined;
  try {
    musicUserToken = await authorize(developerToken);
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : "Apple Music couldn't start. Try again." };
  }
  if (!musicUserToken) return 'cancelled';

  try {
    const status = unwrap(
      await api.PUT('/me/music/{provider}', {
        params: { path: { provider: 'apple_music' } },
        body: { music_user_token: musicUserToken },
      }),
    );
    return { ok: true, status };
  } catch (error) {
    return { ok: false, message: connectErrorMessage(error, 'apple_music') };
  }
}

export function connectErrorMessage(error: unknown, provider?: MusicProvider): string {
  const name = provider ? PROVIDER_NAMES[provider] : 'This service';
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'music_not_connected':
        return `${name} isn't connected. Connect it first.`;
      case 'music_reauth_required':
        return `${name} stopped accepting this connection. Connect again to keep syncing.`;
      case 'music_in_use':
        return `${provider ? `This ${name}` : 'This'} account is already connected to another Fandom Score account.`;
      case 'music_unavailable':
        return `${name} isn't available right now, or isn't set up on this server. Try again in a few minutes.`;
    }
    const fieldProblem = error.fieldError('code') ?? error.fieldError('music_user_token');
    if (fieldProblem) {
      return fieldProblem;
    }
  }
  return errorMessage(error);
}

/** One line describing the connection for the UI. */
export function describeStatus(status: MusicStatus): string {
  const name = PROVIDER_NAMES[status.provider];
  if (!status.connected) {
    return 'Not connected.';
  }
  switch (status.sync_state) {
    case 'queued':
    case 'running':
      return 'Syncing your library…';
    case 'reauth_required':
      return `${name} stopped accepting this connection. Connect again to keep syncing.`;
    case 'failed':
      return status.sync_error === 'provider_forbidden'
        ? `${name} refused this account. Connect again, or check the account's subscription.`
        : "The last sync didn't finish. It will try again automatically.";
    default:
      if (!status.last_synced_at) {
        return 'Connected. Waiting for the first sync.';
      }
      return `Last synced ${new Date(status.last_synced_at).toLocaleString()}. Found ${status.tracked_artists_found} tracked artists in your library.`;
  }
}

export function isSyncing(status: MusicStatus | null | undefined): boolean {
  return status?.sync_state === 'queued' || status?.sync_state === 'running';
}
