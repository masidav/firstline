import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import { type DrawStatus, errorMessage, type PromoterRegistration, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { can, download, formatMoney, toCsv } from '../lib/format';
import { formatInZone } from '../lib/time';
import { useLoad } from '../lib/useLoad';
import { Button, Card, ErrorText, Loading, Status } from '../ui/components';
import { AllocationForm } from './AllocationForm';
import { useOrganization } from './OrganizationLayout';

/** How often the page refreshes while fans are registering, and while the draw is queued or running. */
export const LIVE_REFRESH_MS = 10_000;
export const DRAW_REFRESH_MS = 3_000;

/**
 * One allocation: who registered (live while registration is open), the draw (queued by an admin once registration
 * closes, run by the worker), who holds seats, and a CSV export. Fans appear as handles and status only.
 */
export function AllocationPage() {
  const { organization } = useOrganization();
  const { eventId = '', allocationId = '' } = useParams();
  const { session } = useSession();
  const organizationId = organization.id;
  const manage = can.manage(organization.my_role);

  const show = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/events/{eventId}', { params: { path: { organizationId, eventId } } })),
    [session, organizationId, eventId],
  );
  const path = { params: { path: { organizationId, allocationId } } };
  const registrations = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/allocations/{allocationId}/registrations', path)),
    [session, organizationId, allocationId],
  );
  const draw = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/allocations/{allocationId}/selection', path)),
    [session, organizationId, allocationId],
  );
  const reservations = useLoad(
    async () => draw.data?.status === 'done'
      ? unwrap(await session.api.GET('/organizations/{organizationId}/allocations/{allocationId}/reservations', path)).items
      : [],
    [session, organizationId, allocationId, draw.data?.status],
  );
  const [editing, setEditing] = useState(false);
  const [queueing, setQueueing] = useState(false);
  const [drawError, setDrawError] = useState<string>();

  const allocation = show.data?.allocations.find((a) => a.id === allocationId);
  const drawing = draw.data?.status === 'queued' || draw.data?.status === 'running';
  const live = allocation?.status === 'open' || allocation?.status === 'upcoming';

  // Near real time: refresh while fans register, faster while the draw runs.
  useEffect(() => {
    if (!live && !drawing) return;
    const timer = setInterval(() => {
      void registrations.reload();
      if (drawing) {
        void draw.reload();
        void show.reload();
      }
    }, drawing ? DRAW_REFRESH_MS : LIVE_REFRESH_MS);
    return () => clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live, drawing]);

  if (show.error) return <ErrorText>{show.error}</ErrorText>;
  if (!show.data) return <Loading />;
  if (!allocation) return <ErrorText>This show has no allocation with this id.</ErrorText>;
  const { event } = show.data;
  const timeZone = event.time_zone ?? 'UTC';
  const items = registrations.data?.items ?? [];
  const activeItems = items.filter((r) => r.status !== 'withdrawn');
  const seatsAsked = activeItems.reduce((sum, r) => sum + (r.plus_one ? 2 : 1), 0);
  const reservationByHandle = new Map((reservations.data ?? []).map((r) => [r.handle, r.reservation]));

  async function queueDraw() {
    setQueueing(true);
    setDrawError(undefined);
    try {
      draw.setData(unwrap(await session.api.POST('/organizations/{organizationId}/allocations/{allocationId}/selection', path)));
    } catch (e) {
      setDrawError(errorMessage(e));
    } finally {
      setQueueing(false);
    }
  }

  function exportCsv() {
    const rows = [
      ['handle', 'status', 'local', 'plus_one', 'rank', 'seats', 'registered_at', 'withdrawn_at', 'reservation'],
      ...items.map((r: PromoterRegistration) => [
        r.handle, r.status, r.local, r.plus_one, r.selection_rank, r.seats, r.registered_at, r.withdrawn_at,
        reservationByHandle.get(r.handle)?.status ?? '',
      ]),
    ];
    const slug = `${event.name}-${allocation!.name}`.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
    download(`${slug}-registrations.csv`, toCsv(rows));
  }

  return (
    <>
      <p><Link to={`/o/${organizationId}/shows/${eventId}`}>← {event.name}</Link></p>
      <Card
        title={<>{allocation.name} <Status kind="allocation" value={allocation.status} /></>}
        actions={manage && ['draft', 'upcoming'].includes(allocation.status) && !editing && <Button kind="secondary" onClick={() => setEditing(true)}>Edit rules</Button>}
      >
        <p className="muted">
          {allocation.tickets} tickets at {formatMoney(allocation.face_value_cents, allocation.currency)} · minimum raw score{' '}
          {allocation.min_raw_score ?? 'set when published'} · registration {formatInZone(allocation.registration_opens_at, timeZone)} to{' '}
          {formatInZone(allocation.registration_closes_at, timeZone)}
        </p>
        {editing && (
          <AllocationForm
            timeZone={timeZone}
            existing={allocation}
            submitLabel="Save rules"
            onCancel={() => setEditing(false)}
            onSubmit={async (body) => {
              try {
                unwrap(await session.api.PATCH('/organizations/{organizationId}/allocations/{allocationId}', { ...path, body }));
              } catch (e) {
                throw new Error(errorMessage(e));
              }
              setEditing(false);
              await show.reload();
            }}
          />
        )}
        <div className="stats" aria-label="Registration totals">
          <div className="stat"><strong>{activeItems.length}</strong>registered</div>
          <div className="stat"><strong>{activeItems.filter((r) => r.local).length}</strong>local</div>
          <div className="stat"><strong>{activeItems.filter((r) => r.plus_one).length}</strong>with a +1</div>
          <div className="stat"><strong>{seatsAsked}</strong>seats asked for</div>
          <div className="stat"><strong>{allocation.tickets}</strong>tickets</div>
        </div>
        {live && <p className="muted">Updates every {LIVE_REFRESH_MS / 1000} seconds while registration is open.</p>}
      </Card>

      <DrawCard
        status={draw.data}
        allocationStatus={allocation.status}
        manage={manage}
        busy={queueing}
        error={drawError ?? draw.error}
        onQueue={() => void queueDraw()}
      />

      <Card title="Registrations" actions={items.length > 0 && <Button kind="secondary" onClick={exportCsv}>Export CSV</Button>}>
        <ErrorText>{registrations.error}</ErrorText>
        {!registrations.data && !registrations.error && <Loading />}
        {registrations.data && items.length === 0 && <p className="muted">No one has registered yet.</p>}
        {items.length > 0 && (
          <table>
            <thead>
              <tr><th>Rank</th><th>Fan</th><th>Status</th><th>Local</th><th>+1</th><th>Seats</th><th>Ticket</th><th>Registered</th></tr>
            </thead>
            <tbody>
              {items.map((r) => (
                <tr key={r.handle}>
                  <td>{r.selection_rank ?? '–'}</td>
                  <td>@{r.handle}</td>
                  <td><Status kind="registration" value={r.status} /></td>
                  <td>{r.local ? 'Yes' : 'No'}</td>
                  <td>{r.plus_one ? 'Yes' : 'No'}</td>
                  <td>{r.seats ?? '–'}</td>
                  <td>{reservationByHandle.get(r.handle) ? <Status kind="reservation" value={reservationByHandle.get(r.handle)!.status} /> : '–'}</td>
                  <td>{formatInZone(r.registered_at, timeZone)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <p className="muted">Fans appear by handle only: no scores, cities, or contact details.</p>
      </Card>
    </>
  );
}

function DrawCard({
  status, allocationStatus, manage, busy, error, onQueue,
}: {
  status: DrawStatus | null; allocationStatus: string; manage: boolean; busy: boolean; error?: string; onQueue: () => void;
}) {
  const state = status?.status ?? 'not_run';
  const canQueue = manage && allocationStatus === 'closed' && (state === 'not_run' || state === 'failed');
  return (
    <Card title={<>The draw <Status kind="draw" value={state} /></>} actions={canQueue && <Button busy={busy} onClick={onQueue}>{state === 'failed' ? 'Try the draw again' : 'Run the draw'}</Button>}>
      {state === 'not_run' && (
        <p className="muted">
          {allocationStatus === 'closed'
            ? 'Registration has closed. Running the draw ranks local fans first, then by fandom score, ties by a recorded random seed.'
            : 'The draw can run once registration closes.'}
        </p>
      )}
      {(state === 'queued' || state === 'running') && <p>The draw is {state}. This page updates by itself; it usually takes seconds.</p>}
      {state === 'failed' && <p className="error">The draw didn't finish: {status?.error ?? 'unknown error'}.</p>}
      {state === 'done' && status?.selection && (
        <p className="good">
          {status.selection.selected_fans} of {status.selection.registrations} fans selected; {status.selection.seats_assigned} of{' '}
          {status.selection.tickets} seats assigned. Seed for the audit: <code>{status.selection.seed}</code>
        </p>
      )}
      <ErrorText>{error}</ErrorText>
    </Card>
  );
}
