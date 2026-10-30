import { useState } from 'react';

import { type AllocationReport, errorMessage, type ReportMetrics, type ShowReport as ShowReportData, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { download, toCsv } from '../lib/format';
import { Button, Card, ErrorText, Field, Loading } from '../ui/components';
import { useLoad } from '../lib/useLoad';

/** The metrics in the order the report lists them, with the label shown for each. */
export const REPORT_ROWS: [keyof ReportMetrics, string][] = [
  ['seats_offered', 'Seats offered'],
  ['registrations', 'Registrations'],
  ['eligible_registrations', 'Eligible'],
  ['ineligible_attempts', 'Ineligible (refused)'],
  ['hub_registrations', 'Hub-city registrations'],
  ['non_hub_registrations', 'Other registrations'],
  ['selected_fans', 'Fans selected'],
  ['selected_in_draw', 'Selected in the draw'],
  ['plus_ones', '+1s'],
  ['seats_filled', 'Seats filled'],
  ['hub_seats_filled', 'Hub-city seats filled'],
  ['non_hub_seats_filled', 'Other seats filled'],
  ['tickets_delivered', 'Tickets delivered'],
  ['returns', 'Returns'],
  ['seats_returned', 'Seats returned'],
  ['re_offers', 'Re-offers'],
  ['seats_re_offered', 'Seats re-offered'],
  ['cancellations', 'Cancelled (account deleted)'],
  ['seats_unfilled', 'Seats unfilled now'],
  ['empty_seats', 'Empty seats at show start'],
  ['sell_through_percent', 'Sell-through %'],
  ['scalping_complaints', 'Scalping complaints'],
];

export function formatMetric(value: number | null | undefined, key: keyof ReportMetrics): string {
  if (value == null) return key === 'empty_seats' ? 'after the show' : key === 'scalping_complaints' ? 'not recorded' : '–';
  return key === 'sell_through_percent' ? `${value.toFixed(1)}%` : String(value);
}

/** The CSV: one column per allocation plus the roll-up, one row per metric. */
export function reportCsv(report: ShowReportData): string {
  const columns = [...report.allocations.map((a) => a.allocation.name), 'Total'];
  const metrics = [...report.allocations.map((a) => a.metrics), report.totals];
  return toCsv([
    ['metric', ...columns],
    ...REPORT_ROWS.map(([key, label]) => [label, ...metrics.map((m) => m[key] ?? '')]),
    ['complaints_note', ...report.allocations.map((a) => a.complaints?.note ?? ''), ''],
  ]);
}

/**
 * The pilot case-study report for a show: every published allocation side by side with the roll-up, a CSV download,
 * and, for admins, the hand-entered scalping complaints.
 */
export function ShowReport({ organizationId, eventId, manage }: { organizationId: string; eventId: string; manage: boolean }) {
  const { session } = useSession();
  const report = useLoad(
    async () => unwrap(await session.api.GET('/organizations/{organizationId}/events/{eventId}/report', { params: { path: { organizationId, eventId } } })),
    [session, organizationId, eventId],
  );
  const data = report.data;

  function exportCsv() {
    if (!data) return;
    const slug = data.event.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
    download(`${slug}-report.csv`, reportCsv(data));
  }

  return (
    <Card title="Case-study report" actions={data && data.allocations.length > 0 && <Button kind="secondary" onClick={exportCsv}>Export report CSV</Button>}>
      <ErrorText>{report.error}</ErrorText>
      {!data && !report.error && <Loading />}
      {data && data.allocations.length === 0 && <p className="muted">The report fills in once an allocation is published.</p>}
      {data && data.allocations.length > 0 && (
        <>
          <p className="muted">
            Seats, not fans, unless the row says fans: a +1 is two seats on one reservation.{' '}
            {data.show_started ? 'The show has started, so unfilled seats are empty seats.' : 'Empty seats are counted at show start.'}
          </p>
          <table aria-label="Case-study report">
            <thead>
              <tr>
                <th>Metric</th>
                {data.allocations.map((a) => <th key={a.allocation.id}>{a.allocation.name}</th>)}
                <th>Total</th>
              </tr>
            </thead>
            <tbody>
              {REPORT_ROWS.map(([key, label]) => (
                <tr key={key}>
                  <td>{label}</td>
                  {data.allocations.map((a) => <td key={a.allocation.id}>{formatMetric(a.metrics[key], key)}</td>)}
                  <td><strong>{formatMetric(data.totals[key], key)}</strong></td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.allocations.map((a) => (
            <Complaints
              key={a.allocation.id}
              organizationId={organizationId}
              report={a}
              manage={manage}
              onSaved={() => void report.reload()}
            />
          ))}
        </>
      )}
    </Card>
  );
}

/** Scalping complaints for one allocation: what is recorded, and a form for admins to replace it. */
function Complaints({ organizationId, report, manage, onSaved }: { organizationId: string; report: AllocationReport; manage: boolean; onSaved: () => void }) {
  const { session } = useSession();
  const [editing, setEditing] = useState(false);
  const [count, setCount] = useState(String(report.complaints?.scalping_complaints ?? 0));
  const [note, setNote] = useState(report.complaints?.note ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const recorded = report.complaints;

  async function save() {
    setBusy(true);
    setError(undefined);
    try {
      unwrap(await session.api.PUT('/organizations/{organizationId}/allocations/{allocationId}/complaints', {
        params: { path: { organizationId, allocationId: report.allocation.id } },
        body: { scalping_complaints: Number(count), note: note.trim() || null },
      }));
      setEditing(false);
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="complaints">
      <p>
        <strong>{report.allocation.name}</strong> scalping complaints:{' '}
        {recorded
          ? <>{recorded.scalping_complaints}{recorded.note ? ` — ${recorded.note}` : ''} <span className="muted">(by @{recorded.updated_by_handle})</span></>
          : <span className="muted">not recorded. Tickets never leave the account they were drawn for, so complaints come from outside; enter the ones that reach you.</span>}
        {manage && !editing && <> <Button kind="secondary" onClick={() => setEditing(true)}>{recorded ? 'Update complaints' : 'Record complaints'}</Button></>}
      </p>
      {editing && (
        <form className="grid" onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <Field label={`Scalping complaints for ${report.allocation.name}`} type="number" min={0} value={count} onChange={(e) => setCount(e.target.value)} />
          <Field label="Note" value={note} onChange={(e) => setNote(e.target.value)} hint="Optional: where the complaints came from and what was found." />
          <div className="actions">
            <Button type="submit" busy={busy}>Save complaints</Button>
            <Button kind="secondary" onClick={() => setEditing(false)}>Cancel</Button>
          </div>
          <ErrorText>{error}</ErrorText>
        </form>
      )}
    </div>
  );
}
