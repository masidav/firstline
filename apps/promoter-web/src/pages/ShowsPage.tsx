import { Link } from 'react-router-dom';

import { unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { useArtistNames } from '../lib/artists';
import { can } from '../lib/format';
import { formatInZone } from '../lib/time';
import { useLoad } from '../lib/useLoad';
import { Card, ErrorText, Loading, Status } from '../ui/components';
import { useOrganization } from './OrganizationLayout';

/** The organization's shows, newest first, each with its allocations' state. */
export function ShowsPage() {
  const { organization } = useOrganization();
  const { session } = useSession();
  const organizationId = organization.id;
  const { data, error } = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/events', { params: { path: { organizationId } } })).items,
    [session, organizationId],
  );
  const artists = useArtistNames(session.api, data?.map((d) => d.event.artist_id) ?? []);

  return (
    <Card
      title="Shows"
      actions={can.manage(organization.my_role) && <Link className="button primary" to={`/o/${organizationId}/shows/new`}>New show</Link>}
    >
      <ErrorText>{error}</ErrorText>
      {!data && !error && <Loading />}
      {data?.length === 0 && <p className="muted">No shows yet.{can.manage(organization.my_role) ? ' Add your first one.' : ''}</p>}
      {data && data.length > 0 && (
        <table>
          <thead>
            <tr><th>Show</th><th>Artist</th><th>Where</th><th>When</th><th>Fan allocations</th></tr>
          </thead>
          <tbody>
            {data.map(({ event, allocations }) => (
              <tr key={event.id}>
                <td><Link to={`/o/${organizationId}/shows/${event.id}`}>{event.name}</Link></td>
                <td>{artists[event.artist_id] ?? '…'}</td>
                <td>{[event.venue, event.city].filter(Boolean).join(', ')}</td>
                <td>{formatInZone(event.starts_at, event.time_zone)}</td>
                <td>
                  {allocations.length === 0 ? <span className="muted">None</span> : (
                    <div className="actions">{allocations.map((a) => <Status key={a.id} kind="allocation" value={a.status} />)}</div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}
