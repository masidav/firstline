import createClient from 'openapi-fetch';

import type { components, paths } from './schema';

// Types come from openapi.yaml (`npm run generate:api`), like the fan app, so both share one contract.
export type { components };
type Schemas = components['schemas'];
export type Me = Schemas['Me'];
export type Session = Schemas['Session'];
export type Problem = Schemas['Problem'];
export type Organization = Schemas['Organization'];
export type Role = Schemas['OrganizationMember']['role'];
export type EventDetail = Schemas['EventDetail'];
export type Show = Schemas['Event'];
export type Allocation = Schemas['Allocation'];
export type DrawStatus = Schemas['DrawStatus'];
export type PromoterRegistration = Schemas['PromoterRegistration'];
export type Reservation = Schemas['Reservation'];
export type Selection = Schemas['Selection'];
export type ShowReport = Schemas['ShowReport'];
export type AllocationReport = Schemas['AllocationReport'];
export type ReportMetrics = Schemas['ReportMetrics'];
export type HubCity = Schemas['HubCity'];
export type Artist = Schemas['Artist'];

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

/** A short message for any failed call. */
export function errorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return "Can't reach the server. Check your connection and try again.";
  }
  if (error.code === 'unauthorized') return 'Your session has ended. Sign in again.';
  if (error.code === 'rate_limited') return 'Too many attempts. Wait a minute and try again.';
  const fields = error.problem.errors?.map((e) => e.message);
  return fields?.length ? fields.join(' ') : error.problem.detail ?? error.message;
}
