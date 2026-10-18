import createClient from 'openapi-fetch';

import type { components, paths } from './schema';

// Types come from openapi.yaml (`npm run generate:api`), so the app and the API share one contract.
export type Me = components['schemas']['Me'];
export type Session = components['schemas']['Session'];
export type Problem = components['schemas']['Problem'];

export type FetchLike = (request: Request) => Promise<Response>;

export function createApi(baseUrl: string, fetchImpl: FetchLike) {
  return createClient<paths>({ baseUrl, fetch: fetchImpl });
}

export type Api = ReturnType<typeof createApi>;

/** An API error response (RFC 9457 problem+json with a stable `code`). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Partial<Problem>,
  ) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`);
    this.name = 'ApiError';
  }

  get code(): string | undefined {
    return this.problem.code;
  }

  /** The message for one invalid field, if the API reported one. */
  fieldError(field: string): string | undefined {
    return this.problem.errors?.find((error) => error.field === field)?.message;
  }
}

/** Returns the response body, or throws ApiError for an error status. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok) {
    const problem = typeof result.error === 'object' && result.error !== null ? result.error : {};
    throw new ApiError(result.response.status, problem as Partial<Problem>);
  }
  return result.data as T;
}
