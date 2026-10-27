import { type Api, ApiError, createApi, type FetchLike, type Me, type Session, unwrap } from '../api/client';

// The same session rules as the fan app (apps/mobile/src/auth/session.ts, docs/adr/0003-authentication.md): the
// access token stays in memory, the refresh token in localStorage (this site loads no third-party scripts), one
// refresh at a time (a reused refresh token ends the session), and one retry after a 401.

export type SessionState =
  | { status: 'loading' }
  | { status: 'signedOut' }
  | { status: 'signedIn'; me: Me }
  | { status: 'unreachable' };

/** What a sign-in hands the API: a provider identity token, and the nonce in it if any. */
export interface Credential {
  provider: 'google' | 'apple';
  idToken: string;
  nonce?: string;
}

export interface TokenStore {
  get(): string | null;
  set(token: string): void;
  clear(): void;
}

const KEY = 'fandom-score-promoter.refresh-token';

export const browserTokenStore: TokenStore = {
  get: () => globalThis.localStorage?.getItem(KEY) ?? null,
  set: (token) => globalThis.localStorage?.setItem(KEY, token),
  clear: () => globalThis.localStorage?.removeItem(KEY),
};

export class SessionManager {
  readonly api: Api;
  private readonly bareApi: Api;
  private accessToken: string | null = null;
  private refreshing: Promise<boolean> | null = null;
  private state: SessionState = { status: 'loading' };
  private readonly listeners = new Set<() => void>();

  constructor(baseUrl: string, private readonly store: TokenStore = browserTokenStore, private readonly fetchImpl: FetchLike = (r) => fetch(r)) {
    this.bareApi = createApi(baseUrl, fetchImpl);
    this.api = createApi(baseUrl, (request) => this.authorizedFetch(request));
  }

  getState = (): SessionState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  async restore(): Promise<void> {
    this.setState({ status: 'loading' });
    try {
      if (!(await this.refresh())) this.setState({ status: 'signedOut' });
    } catch {
      this.setState({ status: 'unreachable' });
    }
  }

  /** A new account needs a handle: without one this throws ApiError with fieldError('handle') set. */
  async signIn(credential: Credential, handle?: string): Promise<void> {
    const session = unwrap(
      await this.bareApi.POST('/auth/sessions', {
        body: { provider: credential.provider, id_token: credential.idToken, nonce: credential.nonce, handle },
      }),
    );
    this.accept(session);
  }

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
      this.clear();
    }
  }

  private async doRefresh(): Promise<boolean> {
    const refreshToken = this.store.get();
    if (!refreshToken) return false;
    const result = await this.bareApi.POST('/auth/refresh', { body: { refresh_token: refreshToken } });
    if (result.response.status === 401) {
      this.clear();
      return false;
    }
    this.accept(unwrap(result));
    return true;
  }

  private async authorizedFetch(request: Request): Promise<Response> {
    const retry = request.clone();
    const response = await this.fetchImpl(this.withToken(request));
    if (response.status !== 401 || this.state.status !== 'signedIn') return response;
    return (await this.refresh().catch(() => false)) ? this.fetchImpl(this.withToken(retry)) : response;
  }

  private withToken(request: Request): Request {
    const authorized = new Request(request);
    if (this.accessToken) authorized.headers.set('Authorization', `Bearer ${this.accessToken}`);
    return authorized;
  }

  private accept(session: Session): void {
    this.accessToken = session.access_token;
    this.store.set(session.refresh_token);
    this.setState({ status: 'signedIn', me: session.user });
  }

  private clear(): void {
    this.accessToken = null;
    this.store.clear();
    this.setState({ status: 'signedOut' });
  }

  private setState(state: SessionState): void {
    this.state = state;
    this.listeners.forEach((listener) => listener());
  }
}

export { ApiError };
