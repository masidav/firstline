import { NavLink, Outlet, useOutletContext, useParams } from 'react-router-dom';

import { type Organization, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { ROLE_LABELS } from '../lib/format';
import { useLoad } from '../lib/useLoad';
import { Button, ErrorText, Loading } from '../ui/components';

export interface OrganizationContext {
  organization: Organization;
  reloadOrganization: () => Promise<void>;
}

/** The organization's pages share its name, the promoter's role, and the navigation. */
export function OrganizationLayout() {
  const { organizationId = '' } = useParams();
  const { session, state } = useSession();
  const { data, error, reload } = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}', { params: { path: { organizationId } } })),
    [session, organizationId],
  );

  return (
    <>
      <header className="topbar">
        <NavLink className="brand" to="/">Fandom Score</NavLink>
        <nav aria-label="Organization">
          {data && <span><strong>{data.name}</strong> · {ROLE_LABELS[data.my_role ?? ''] ?? ''}</span>}
          <NavLink to={`/o/${organizationId}`} end>Shows</NavLink>
          <NavLink to={`/o/${organizationId}/members`}>Members</NavLink>
        </nav>
        <span className="muted">@{state.status === 'signedIn' ? state.me.handle : ''}</span>
        <Button kind="secondary" onClick={() => void session.signOut()}>Sign out</Button>
      </header>
      <main className="page">
        <ErrorText>{error}</ErrorText>
        {!data && !error && <Loading />}
        {data && <Outlet context={{ organization: data, reloadOrganization: reload } satisfies OrganizationContext} />}
      </main>
    </>
  );
}

export function useOrganization(): OrganizationContext {
  return useOutletContext<OrganizationContext>();
}
