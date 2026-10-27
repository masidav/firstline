import { Link, Navigate } from 'react-router-dom';

import { type Organization, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { ROLE_LABELS } from '../lib/format';
import { useLoad } from '../lib/useLoad';
import { Button, ErrorText, Loading } from '../ui/components';

/** The organizations the promoter belongs to; with exactly one, straight to it. */
export function OrganizationsPage() {
  const { session, state } = useSession();
  const { data, error } = useLoad(async () => unwrap(await session.api.GET('/me/organizations')).items, [session]);
  const handle = state.status === 'signedIn' ? state.me.handle : '';

  if (data?.length === 1) return <Navigate to={`/o/${data[0]!.id}`} replace />;
  return (
    <main className="page narrow">
      <h1>Your organizations</h1>
      <ErrorText>{error}</ErrorText>
      {!data && !error && <Loading />}
      {data?.length === 0 && (
        <div className="card">
          <p>
            Your account (<strong>@{handle}</strong>) isn't part of a promoter organization yet. Fandom Score staff set
            organizations up; ask them, or your organization's owner, to add <strong>@{handle}</strong>.
          </p>
        </div>
      )}
      {data?.map((organization: Organization) => (
        <Link key={organization.id} to={`/o/${organization.id}`} className="card">
          <h2>{organization.name}</h2>
          <span className="muted">{ROLE_LABELS[organization.my_role ?? ''] ?? 'Member'}</span>
        </Link>
      ))}
      <Button kind="secondary" onClick={() => void session.signOut()}>Sign out</Button>
    </main>
  );
}
