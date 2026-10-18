import { type Api, ApiError, createApi, type FetchLike, type Me, type Session, unwrap } from '../api/client';
import type { TokenStore } from './storage';

export type SessionState =
  | { status: 'loading' }
  | { status: 'signedOut' }
  | { status: 'signedIn'; me: Me }
  /** The API could not be reached; the refresh token is kept so a retry can resume. */
  | { status: 'unreachable' };

export type Provider = 'apple' | 'google';

/**
 * Holds the signed-in session (docs/adr/0003-authentication.md).
 *
 * - The access token (15 minutes) stays in memory; the refresh token goes to the TokenStore.
 * - `api` adds the access token to every call. On a 401 it refreshes once and retries the call.
 * - Refreshes are single-flight: the server treats a reused refresh token as stolen and ends the session,
 *   so two concurrent refreshes with the same token must never happen.
 */
export class SessionManager {
  /** Authenticated client for everything except sign-in and refresh. */
  readonly api: Api;

  private readonly bareApi: Api;
  private accessToken: string | null = null;
  private refreshing: Promise<boolean> | null = null;
  private state: SessionState = { status: 'loading' };
  private readonly listeners = new Set<() => void>();

  constructor(
    baseUrl: string,
    private readonly store: TokenStore,
    private readonly fetchImpl: FetchLike = (request) => fetch(request),
  ) {
    this.bareApi = createApi(baseUrl, fetchImpl);
    this.api = createApi(baseUrl, (request) => this.authorizedFetch(request));
  }

  getState = (): SessionState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** On app start: resume the stored session, if any. */
  async restore(): Promise<void> {
    this.setState({ status: 'loading' });
    try {
      if (!(await this.refresh())) {
        this.setState({ status: 'signedOut' });
      }
    } catch {
      this.setState({ status: 'unreachable' });
    }
  }

  /**
   * Signs in with a provider identity token. For a new account the API needs a handle: without one this
   * throws ApiError with `fieldError('handle')` set.
   */
  async signIn(provider: Provider, idToken: string, handle?: string): Promise<{ isNewUser: boolean }> {
    const session = unwrap(
      await this.bareApi.POST('/auth/sessions', { body: { provider, id_token: idToken, handle } }),
    );
    await this.accept(session);
    return { isNewUser: session.is_new_user };
  }

  /** Gets new tokens. Returns false when there is no usable session. Throws when the API is unreachable. */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.doRefresh().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  async signOut(): Promise<void> {
    try {
      await this.api.DELETE('/auth/sessions/current');
    } finally {
      await this.clear();
    }
  }

  async deleteAccount(appleAuthorizationCode?: string): Promise<void> {
    unwrap(
      await this.api.DELETE('/me', {
        body: appleAuthorizationCode ? { apple_authorization_code: appleAuthorizationCode } : undefined,
      }),
    );
    await this.clear();
  }

  /** After the profile changes (PATCH /me). */
  setMe(me: Me): void {
    if (this.state.status === 'signedIn') {
      this.setState({ status: 'signedIn', me });
    }
  }

  /** Merges a change (e.g. new Spotify status) into the current profile. */
  updateMe(change: Partial<Me>): void {
    if (this.state.status === 'signedIn') {
      this.setState({ status: 'signedIn', me: { ...this.state.me, ...change } });
    }
  }

  private async doRefresh(): Promise<boolean> {
    const refreshToken = await this.store.get();
    if (!refreshToken) {
      return false;
    }

    const result = await this.bareApi.POST('/auth/refresh', { body: { refresh_token: refreshToken } });
    if (result.response.status === 401) {
      await this.clear();
      return false;
    }
    await this.accept(unwrap(result));
    return true;
  }

  private async authorizedFetch(request: Request): Promise<Response> {
    const retry = request.clone();
    const response = await this.fetchImpl(this.withToken(request));
    if (response.status !== 401 || this.state.status !== 'signedIn') {
      return response;
    }
    // The access token expired or the session ended. One refresh, then one retry.
    return (await this.refresh().catch(() => false)) ? this.fetchImpl(this.withToken(retry)) : response;
  }

  private withToken(request: Request): Request {
    const authorized = new Request(request);
    if (this.accessToken) {
      authorized.headers.set('Authorization', `Bearer ${this.accessToken}`);
    }
    return authorized;
  }

  private async accept(session: Session): Promise<void> {
    this.accessToken = session.access_token;
    await this.store.set(session.refresh_token);
    this.setState({ status: 'signedIn', me: session.user });
  }

  private async clear(): Promise<void> {
    this.accessToken = null;
    await this.store.clear();
    this.setState({ status: 'signedOut' });
  }

  private setState(state: SessionState): void {
    this.state = state;
    this.listeners.forEach((listener) => listener());
  }
}

export { ApiError };
