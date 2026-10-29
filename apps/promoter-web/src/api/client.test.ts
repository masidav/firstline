// @vitest-environment node
import { ApiError, errorMessage, unwrap } from './client';

function result(status: number, body?: unknown) {
  const response = new Response(null, { status });
  return status < 400 ? { data: body, response } : { error: body, response };
}

test('unwrap returns the body of a successful call', () => {
  expect(unwrap(result(200, { id: 'x' }))).toEqual({ id: 'x' });
});

test('unwrap throws ApiError with the problem code and field errors', () => {
  const problem = { code: 'validation_failed', detail: 'Check the form.', errors: [{ field: 'name', message: 'Required.' }] };
  const error = (() => {
    try {
      unwrap(result(422, problem));
    } catch (e) {
      return e;
    }
    return undefined;
  })() as ApiError;

  expect(error).toBeInstanceOf(ApiError);
  expect(error.status).toBe(422);
  expect(error.code).toBe('validation_failed');
  expect(error.message).toBe('Check the form.');
  expect(error.fieldError('name')).toBe('Required.');
  expect(error.fieldError('other')).toBeUndefined();
});

test('a non-JSON error body still becomes an ApiError', () => {
  const error = (() => {
    try {
      unwrap(result(502, 'Bad Gateway'));
    } catch (e) {
      return e;
    }
    return undefined;
  })() as ApiError;

  expect(error.status).toBe(502);
  expect(error.code).toBeUndefined();
  expect(error.message).toBe('Request failed (502)');
});

test('errorMessage explains network, session, rate-limit, and field errors', () => {
  expect(errorMessage(new TypeError('Failed to fetch'))).toBe("Can't reach the server. Check your connection and try again.");
  expect(errorMessage(new ApiError(401, { code: 'unauthorized' }))).toBe('Your session has ended. Sign in again.');
  expect(errorMessage(new ApiError(429, { code: 'rate_limited' }))).toBe('Too many attempts. Wait a minute and try again.');
  expect(
    errorMessage(new ApiError(422, { code: 'validation_failed', errors: [{ field: 'a', message: 'A is wrong.' }, { field: 'b', message: 'B too.' }] })),
  ).toBe('A is wrong. B too.');
  expect(errorMessage(new ApiError(409, { code: 'conflict', detail: 'Already there.' }))).toBe('Already there.');
  expect(errorMessage(new ApiError(500, { code: 'internal_error' }))).toBe('Request failed (500)');
});
