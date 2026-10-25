import { type Api, type components, unwrap } from '../../api/client';

export type UserSummary = components['schemas']['UserSummary'];

/** Follow or unfollow a fan (PUT/DELETE /users/{handle}/follow). Both are idempotent on the server. */
export async function setFollowing(api: Api, handle: string, follow: boolean): Promise<void> {
  const params = { params: { path: { handle } } };
  unwrap(follow ? await api.PUT('/users/{handle}/follow', params) : await api.DELETE('/users/{handle}/follow', params));
}

/** "Ana" or "@ana": the display name when there is one. */
export function fanName(user: { handle?: string | null; display_name?: string | null } | null | undefined): string {
  if (!user) return 'someone';
  return user.display_name || (user.handle ? `@${user.handle}` : 'someone');
}
