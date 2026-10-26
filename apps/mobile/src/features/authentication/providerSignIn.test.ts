/**
 * @jest-environment node
 */
import { ApiError } from '../../api/client';
import {
  completeGoogleSignIn,
  googleAuthorizeUrl,
  parseGoogleRedirect,
  signInErrorMessage,
  suggestHandle,
} from './providerSignIn';

const pending = { state: 's1', nonce: 'n1', redirectUri: 'http://localhost:8081/google-callback' };

test('the web asks Google for an ID token in the fragment, with state and nonce', () => {
  const url = new URL(
    googleAuthorizeUrl({ clientId: 'web-1', redirectUri: pending.redirectUri, state: 's1', nonce: 'n1', response: { type: 'id_token' } }),
  );

  expect(url.origin + url.pathname).toBe('https://accounts.google.com/o/oauth2/v2/auth');
  expect(Object.fromEntries(url.searchParams)).toEqual({
    client_id: 'web-1',
    redirect_uri: pending.redirectUri,
    response_type: 'id_token',
    scope: 'openid email profile',
    state: 's1',
    nonce: 'n1',
    prompt: 'select_account',
  });
});

test('the native app asks for a code with a PKCE challenge', () => {
  const url = new URL(
    googleAuthorizeUrl({ clientId: 'ios-1', redirectUri: 'com.googleusercontent.apps.ios-1:/oauth2redirect', state: 's', nonce: 'n', response: { type: 'code', challenge: 'ch' } }),
  );

  expect(url.searchParams.get('response_type')).toBe('code');
  expect(url.searchParams.get('code_challenge')).toBe('ch');
  expect(url.searchParams.get('code_challenge_method')).toBe('S256');
});

test('the redirect is read from the fragment (web) or the query (native)', () => {
  expect(parseGoogleRedirect('http://localhost:8081/google-callback#state=s1&id_token=t.o.k&authuser=0')).toEqual({
    id_token: 't.o.k',
    state: 's1',
    code: undefined,
    error: undefined,
  });
  expect(parseGoogleRedirect('com.googleusercontent.apps.x:/oauth2redirect?code=4%2Fabc&state=s1')).toMatchObject({ code: '4/abc', state: 's1' });
  expect(parseGoogleRedirect('http://localhost:8081/google-callback?error=access_denied#')).toMatchObject({ error: 'access_denied' });
});

test('a matching state yields the credential with the nonce the API should check', async () => {
  await expect(completeGoogleSignIn({ id_token: 'tok', state: 's1' }, pending)).resolves.toEqual({
    ok: true,
    credential: { provider: 'google', idToken: 'tok', nonce: 'n1' },
  });
});

test('a redirect that did not start here, or with another state, is refused', async () => {
  await expect(completeGoogleSignIn({ id_token: 'tok', state: 's1' }, null)).resolves.toMatchObject({ ok: false });
  await expect(completeGoogleSignIn({ id_token: 'tok', state: 'other' }, pending)).resolves.toMatchObject({ ok: false });
});

test('a code is exchanged for an ID token; a failed exchange is a plain message', async () => {
  const exchange = jest.fn().mockResolvedValue('from-code');
  await expect(completeGoogleSignIn({ code: 'c', state: 's1' }, pending, exchange)).resolves.toEqual({
    ok: true,
    credential: { provider: 'google', idToken: 'from-code', nonce: 'n1' },
  });
  expect(exchange).toHaveBeenCalledWith('c', pending);

  await expect(completeGoogleSignIn({ code: 'c', state: 's1' }, pending, jest.fn().mockRejectedValue(new Error('boom')))).resolves.toEqual({
    ok: false,
    message: "Couldn't finish signing in with Google. Try again.",
  });
});

test('backing out on Google is explained', async () => {
  await expect(completeGoogleSignIn({ error: 'access_denied' }, pending)).resolves.toEqual({
    ok: false,
    message: "You didn't continue with Google.",
  });
});

test('sign-in failures get a message about signing in, not about the session', () => {
  expect(signInErrorMessage(new ApiError(401, { code: 'unauthorized' }))).toBe('That sign-in expired. Try again.');
  expect(signInErrorMessage(new ApiError(422, { code: 'validation_failed', errors: [{ field: 'provider', message: 'Not enabled.' }] }))).toBe(
    "This way of signing in isn't available right now.",
  );
  expect(
    signInErrorMessage(new ApiError(422, { code: 'validation_failed', title: 'Verified email required', detail: 'The sign-in provider did not share a verified email address.' })),
  ).toBe('The sign-in provider did not share a verified email address.');
  expect(signInErrorMessage(new ApiError(409, { code: 'email_in_use' }))).toBe(
    'Another account already uses this email. Sign in the way you did before.',
  );
});

test('a handle is suggested from the token email, cleaned to the allowed characters', () => {
  const token = (claims: object) => `h.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.sig`;
  expect(suggestHandle(token({ email: 'alice.fan+tix@example.test' }))).toBe('alice_fan_tix');
  expect(suggestHandle(token({ name: 'Bo' }))).toBe('');
  expect(suggestHandle(token({ name: 'Émile Zola' }))).toBe('mile_Zola');
  expect(suggestHandle('dev:alice')).toBe('');
  expect(suggestHandle('a.not-base64!.c')).toBe('');
});
