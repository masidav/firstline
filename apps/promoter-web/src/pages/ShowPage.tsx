import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { type Allocation, errorMessage, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { useArtistNames } from '../lib/artists';
import { can, formatMoney } from '../lib/format';
import { formatInZone, isoToZoned, zonedToIso } from '../lib/time';
import { useLoad } from '../lib/useLoad';
import { Button, Card, ErrorText, Field, Loading, Status } from '../ui/components';
import { AllocationForm } from './AllocationForm';
import { useOrganization } from './OrganizationLayout';

/** One show: its details (editable until an allocation opens) and its fan allocations. */
export function ShowPage() {
  const { organization } = useOrganization();
  const { eventId = '' } = useParams();
  const { session } = useSession();
  const organizationId = organization.id;
  const path = { organizationId, eventId };
  const { data, error, reload } = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/events/{eventId}', { params: { path } })),
    [session, organizationId, eventId],
  );
  const artists = useArtistNames(session.api, data ? [data.event.artist_id] : []);
  const [editing, setEditing] = useState(false);
  const [adding, setAdding] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const manage = can.manage(organization.my_role);

  if (error) return <ErrorText>{error}</ErrorText>;
  if (!data) return <Loading />;
  const { event, allocations } = data;
  const timeZone = event.time_zone ?? 'UTC';

  async function act(id: string, action: () => Promise<unknown>) {
    setBusy(id);
    setActionError(undefined);
    try {
      await action();
      await reload();
    } catch (e) {
      setActionError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const allocationPath = (allocationId: string) => ({ params: { path: { organizationId, allocationId } } });

  return (
    <>
      <Card
        title={event.name}
        actions={manage && !editing && <Button kind="secondary" onClick={() => setEditing(true)}>Edit show</Button>}
      >
        <p className="muted">
          {artists[event.artist_id] ?? '…'} · {[event.venue, event.city, event.region].filter(Boolean).join(', ')} ·{' '}
          {formatInZone(event.starts_at, event.time_zone)}
        </p>
        {editing && (
          <EditShow
            name={event.name}
            venue={event.venue ?? ''}
            startsAt={isoToZoned(event.starts_at, timeZone)}
            timeZone={timeZone}
            onCancel={() => setEditing(false)}
            onSave={async (body) => {
              unwrap(await session.api.PATCH('/organizations/{organizationId}/events/{eventId}', { params: { path }, body }));
              setEditing(false);
              await reload();
            }}
          />
        )}
      </Card>

      <Card
        title="Fan allocations"
        actions={manage && !adding && <Button onClick={() => setAdding(true)}>Add allocation</Button>}
      >
        <ErrorText>{actionError}</ErrorText>
        {adding && (
          <AllocationForm
            timeZone={timeZone}
            submitLabel="Create draft"
            onCancel={() => setAdding(false)}
            onSubmit={async (body) => {
              const result = await session.api.POST('/organizations/{organizationId}/events/{eventId}/allocations', { params: { path }, body });
              if (!result.response.ok) throw new Error(errorMessage(tryUnwrap(result)));
              setAdding(false);
              await reload();
            }}
          />
        )}
        {allocations.length === 0 && !adding && <p className="muted">No allocations yet.</p>}
        {allocations.length > 0 && (
          <table>
            <thead>
              <tr><th>Allocation</th><th>Status</th><th>Tickets</th><th>Face value</th><th>Registration</th><th /></tr>
            </thead>
            <tbody>
              {allocations.map((a: Allocation) => (
                <tr key={a.id}>
                  <td><Link to={`/o/${organizationId}/shows/${eventId}/allocations/${a.id}`}>{a.name}</Link></td>
                  <td><Status kind="allocation" value={a.status} /></td>
                  <td>{a.tickets}</td>
                  <td>{formatMoney(a.face_value_cents, a.currency)}</td>
                  <td>{formatInZone(a.registration_opens_at, timeZone)} to {formatInZone(a.registration_closes_at, timeZone)}</td>
                  <td>
                    {manage && (
                      <div className="actions">
                        {a.status === 'draft' && (
                          <>
                            <Button busy={busy === `publish-${a.id}`} onClick={() => void act(`publish-${a.id}`, async () =>
                              unwrap(await session.api.POST('/organizations/{organizationId}/allocations/{allocationId}/publish', allocationPath(a.id))))}>Publish</Button>
                            <Button kind="secondary" busy={busy === `delete-${a.id}`} onClick={() => void act(`delete-${a.id}`, async () =>
                              unwrap(await session.api.DELETE('/organizations/{organizationId}/allocations/{allocationId}', allocationPath(a.id))))}>Delete draft</Button>
                          </>
                        )}
                        {['upcoming', 'open', 'closed'].includes(a.status) && (
                          <Button kind="danger" busy={busy === `cancel-${a.id}`} onClick={() => {
                            if (window.confirm(`Cancel "${a.name}"? Registered fans are told, and this can't be undone.`)) {
                              void act(`cancel-${a.id}`, async () =>
                                unwrap(await session.api.POST('/organizations/{organizationId}/allocations/{allocationId}/cancel', allocationPath(a.id))));
                            }
                          }}>Cancel</Button>
                        )}
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function tryUnwrap(result: { data?: unknown; error?: unknown; response: Response }): unknown {
  try {
    return unwrap(result);
  } catch (e) {
    return e;
  }
}

function EditShow({
  name, venue, startsAt, timeZone, onSave, onCancel,
}: {
  name: string; venue: string; startsAt: string; timeZone: string;
  onSave: (body: { name: string; venue: string | null; starts_at: string }) => Promise<void>;
  onCancel: () => void;
}) {
  const [values, setValues] = useState({ name, venue, startsAt });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  return (
    <form className="grid" onSubmit={(e) => {
      e.preventDefault();
      setBusy(true);
      setError(undefined);
      onSave({ name: values.name.trim(), venue: values.venue.trim() || null, starts_at: zonedToIso(values.startsAt, timeZone) })
        .catch((err: unknown) => setError(errorMessage(err)))
        .finally(() => setBusy(false));
    }}>
      <Field label="Show name" value={values.name} onChange={(e) => setValues({ ...values, name: e.target.value })} />
      <Field label="Venue" value={values.venue} onChange={(e) => setValues({ ...values, venue: e.target.value })} />
      <Field label="Starts" type="datetime-local" value={values.startsAt} onChange={(e) => setValues({ ...values, startsAt: e.target.value })} hint={`Show time zone: ${timeZone}. Locked once an allocation opens.`} />
      <div className="actions">
        <Button type="submit" busy={busy}>Save</Button>
        <Button kind="secondary" onClick={onCancel}>Cancel</Button>
      </div>
      <ErrorText>{error}</ErrorText>
    </form>
  );
}
