import { useState } from 'react';

import type { Allocation } from '../api/client';
import { isoToZoned, zonedToIso } from '../lib/time';
import { Button, ErrorText, Field } from '../ui/components';

export interface AllocationInput {
  name: string;
  tickets: number;
  face_value_cents: number;
  currency: string;
  min_raw_score?: number;
  registration_opens_at: string;
  registration_closes_at: string;
  delivery_hours_before_show: number;
}

/**
 * An allocation's rules: seats, face value, the threshold (blank: the two-saved-albums default, set when published),
 * the registration window in the show's time zone, and when tickets are delivered. Rules lock once registration opens.
 */
export function AllocationForm({
  timeZone, existing, submitLabel, onSubmit, onCancel,
}: {
  timeZone: string;
  existing?: Allocation;
  submitLabel: string;
  onSubmit: (input: AllocationInput) => Promise<void>;
  onCancel?: () => void;
}) {
  const [name, setName] = useState(existing?.name ?? 'Fan allocation');
  const [tickets, setTickets] = useState(String(existing?.tickets ?? 100));
  const [price, setPrice] = useState(existing ? (existing.face_value_cents / 100).toFixed(2) : '');
  const [currency, setCurrency] = useState(existing?.currency ?? 'USD');
  const [threshold, setThreshold] = useState(existing?.min_raw_score == null ? '' : String(existing.min_raw_score));
  const [opens, setOpens] = useState(existing ? isoToZoned(existing.registration_opens_at, timeZone) : '');
  const [closes, setCloses] = useState(existing ? isoToZoned(existing.registration_closes_at, timeZone) : '');
  const [delivery, setDelivery] = useState(String(existing?.delivery_hours_before_show ?? 24));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function submit() {
    const ticketCount = Number(tickets);
    const cents = Math.round(Number(price) * 100);
    if (!Number.isInteger(ticketCount) || ticketCount < 1) return setError('Tickets: a whole number, at least 1.');
    if (!Number.isFinite(cents) || cents < 0 || price.trim() === '') return setError('Face value: an amount, like 45.00.');
    if (!opens || !closes) return setError('Choose when registration opens and closes.');
    setBusy(true);
    setError(undefined);
    try {
      await onSubmit({
        name: name.trim() || 'Fan allocation',
        tickets: ticketCount,
        face_value_cents: cents,
        currency: currency.trim().toUpperCase(),
        min_raw_score: threshold.trim() === "" ? undefined : Number(threshold),
        registration_opens_at: zonedToIso(opens, timeZone),
        registration_closes_at: zonedToIso(closes, timeZone),
        delivery_hours_before_show: Number(delivery),
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="grid" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Field label="Name" value={name} onChange={(e) => setName(e.target.value)} />
      <Field label="Tickets" type="number" min={1} value={tickets} onChange={(e) => setTickets(e.target.value)} hint="One per selected fan, two with a +1." />
      <Field label="Face value" inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} placeholder="45.00" />
      <Field label="Currency" value={currency} onChange={(e) => setCurrency(e.target.value)} maxLength={3} />
      <Field label="Minimum raw score" inputMode="decimal" value={threshold} onChange={(e) => setThreshold(e.target.value)} hint="Blank: a fan with two saved albums, set when published." />
      <Field label="Registration opens" type="datetime-local" value={opens} onChange={(e) => setOpens(e.target.value)} hint={`Show time zone: ${timeZone}.`} />
      <Field label="Registration closes" type="datetime-local" value={closes} onChange={(e) => setCloses(e.target.value)} />
      <Field label="Tickets delivered (hours before the show)" type="number" min={0} value={delivery} onChange={(e) => setDelivery(e.target.value)} />
      <div className="actions">
        <Button type="submit" busy={busy}>{submitLabel}</Button>
        {onCancel && <Button kind="secondary" onClick={onCancel}>Cancel</Button>}
      </div>
      <ErrorText>{error}</ErrorText>
    </form>
  );
}
