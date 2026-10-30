import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { dateLabel, dateTimeLabel, FLAG_REASONS, type FlagReason, prettyJson, reasonLabel, STATUS_LABELS } from './fraudText';

type FraudFlag = components['schemas']['FraudFlag'];
type AuditEntry = components['schemas']['EligibilityAuditEntry'];
type Filter = 'needs_action' | 'open' | 'upheld' | 'cleared';

const FILTERS: { value: Filter; label: string }[] = [
  { value: 'needs_action', label: 'Needs action' },
  { value: 'open', label: 'Open' },
  { value: 'upheld', label: 'Upheld' },
  { value: 'cleared', label: 'Cleared' },
];

const AUDIT_ACTIONS: Record<AuditEntry['action'], string> = {
  registered: 'Registered',
  registration_blocked: 'Registration blocked',
  draw_held_out: 'Held out of the draw',
  backfill_skipped: 'Skipped for a returned seat',
  flag_raised: 'Flag raised',
  flag_upheld: 'Flag upheld',
  flag_cleared: 'Flag cleared',
  appeal_filed: 'Appeal filed',
  appeal_granted: 'Appeal granted',
  appeal_denied: 'Appeal denied',
};

/** Whether a flag belongs in the list for a filter (the API's rule), so a decided flag can leave the queue. */
function matches(flag: FraudFlag, filter: Filter) {
  return filter === 'needs_action' ? flag.status === 'open' || flag.appeal?.status === 'pending' : flag.status === filter;
}

const who = (user: FraudFlag['user']) => (user.deleted || !user.handle ? 'Deleted user' : `@${user.handle}`);

/** Staff only (route /fraud): fraud flags to decide, appeals to answer, a form to flag a fan, and the eligibility audit. */
export function FraudReviewScreen() {
  const { session } = useSession();
  const [filter, setFilter] = useState<Filter>('needs_action');
  const [flags, setFlags] = useState<FraudFlag[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});

  const load = useCallback(
    async (status: Filter, after?: string) => {
      setError(undefined);
      try {
        const page = unwrap(
          await session.api.GET('/admin/fraud-flags', { params: { query: { status, limit: 50, ...(after ? { cursor: after } : {}) } } }),
        );
        setFlags((current) => (after ? [...(current ?? []), ...page.items] : page.items));
        setCursor(page.next_cursor);
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session],
  );

  useEffect(() => {
    void load(filter);
  }, [load, filter]);

  function replace(updated: FraudFlag) {
    setFlags((current) =>
      current && (matches(updated, filter) ? current.map((f) => (f.id === updated.id ? updated : f)) : current.filter((f) => f.id !== updated.id)),
    );
    setNotes((current) => ({ ...current, [updated.id]: '' }));
  }

  async function act(flag: FraudFlag, call: (text: string | undefined) => Promise<FraudFlag>) {
    setBusy(flag.id);
    setError(undefined);
    try {
      replace(await call(notes[flag.id]?.trim() || undefined));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  const decideFlag = (flag: FraudFlag, decision: 'uphold' | 'clear') =>
    act(flag, async (note) =>
      unwrap(
        await session.api.POST('/admin/fraud-flags/{flagId}/decision', {
          params: { path: { flagId: flag.id } },
          body: note ? { decision, note } : { decision },
        }),
      ),
    );

  const decideAppeal = (flag: FraudFlag, appealId: string, decision: 'grant' | 'deny') =>
    act(flag, async (response) =>
      unwrap(
        await session.api.POST('/admin/fraud-appeals/{appealId}/decision', {
          params: { path: { appealId } },
          body: response ? { decision, response } : { decision },
        }),
      ),
    );

  return (
    <Screen>
      <Title>Fraud review</Title>
      <Choices options={FILTERS} value={filter} onChange={(value) => { setFlags(null); setFilter(value); }} label="Show" />
      <ErrorText>{error}</ErrorText>
      {!flags && !error && <Loading />}
      {flags?.length === 0 && <Body muted>{filter === 'needs_action' ? 'Nothing needs a decision.' : 'No flags here.'}</Body>}
      {flags?.map((flag) => {
        const pendingAppeal = flag.appeal?.status === 'pending' ? flag.appeal : null;
        return (
          <View key={flag.id} style={styles.card}>
            <Text style={styles.title}>
              {who(flag.user)} · {reasonLabel(flag.reason)} · {STATUS_LABELS[flag.status]}
            </Text>
            <Text style={styles.meta}>
              Raised {dateTimeLabel(flag.raised_at)} by {flag.source === 'rule' ? 'a rule' : flag.raised_by ? `@${flag.raised_by}` : 'staff'}
            </Text>
            {flag.shown_to_fan_at === null && flag.status !== 'cleared' && (
              <Text style={styles.text}>The fan doesn't know about this flag. Upholding it tells them.</Text>
            )}
            {flag.context.next_draw_closes_at && flag.status === 'open' && (
              <Text style={styles.text}>Review before {dateTimeLabel(flag.context.next_draw_closes_at)}: they're in a draw.</Text>
            )}
            {flag.note ? <Text style={styles.text}>Note: {flag.note}</Text> : null}
            {flag.decided_at && (
              <Text style={styles.text}>
                {STATUS_LABELS[flag.status]} {dateTimeLabel(flag.decided_at)}
                {flag.decided_by ? ` by @${flag.decided_by}` : ''}
                {flag.decision_note ? `: ${flag.decision_note}` : ''}
              </Text>
            )}
            <Text style={styles.label}>Evidence</Text>
            <Text style={styles.code}>{prettyJson(flag.evidence)}</Text>
            <Text style={styles.label}>Context</Text>
            <Text style={styles.text}>Account created {dateLabel(flag.context.account_created_at)}</Text>
            <Text style={styles.text}>
              Trust:{' '}
              {Object.entries(flag.context.trust_parts)
                .map(([part, value]) => `${part} ${value.toFixed(2)}`)
                .join(', ') || 'none'}
            </Text>
            <Text style={styles.text}>
              Live registrations: {flag.context.live_registrations} · Tickets held: {flag.context.held_reservations}
            </Text>
            {flag.appeal && (
              <View style={styles.section}>
                <Text style={styles.label}>
                  Appeal ({flag.appeal.status}) · filed {dateTimeLabel(flag.appeal.filed_at)}
                </Text>
                <Text style={styles.text}>“{flag.appeal.message}”</Text>
                {flag.appeal.decided_at && (
                  <Text style={styles.meta}>
                    Decided {dateTimeLabel(flag.appeal.decided_at)}
                    {flag.appeal_decided_by ? ` by @${flag.appeal_decided_by}` : ''}
                  </Text>
                )}
                {flag.appeal.response ? <Text style={styles.text}>Response: {flag.appeal.response}</Text> : null}
              </View>
            )}
            {pendingAppeal ? (
              <>
                <Field
                  label="Response to the fan (they see it, optional)"
                  value={notes[flag.id] ?? ''}
                  onChangeText={(text) => setNotes((current) => ({ ...current, [flag.id]: text }))}
                  maxLength={2000}
                  multiline
                  autoCapitalize="sentences"
                />
                <Button label="Grant appeal (clear the flag)" busy={busy === flag.id} onPress={() => void decideAppeal(flag, pendingAppeal.id, 'grant')} />
                <Button label="Deny appeal (uphold the flag)" kind="danger" busy={busy === flag.id} onPress={() => void decideAppeal(flag, pendingAppeal.id, 'deny')} />
              </>
            ) : flag.status !== 'cleared' ? (
              <>
                <Field
                  label="Note (staff only, optional)"
                  value={notes[flag.id] ?? ''}
                  onChangeText={(text) => setNotes((current) => ({ ...current, [flag.id]: text }))}
                  maxLength={2000}
                  autoCapitalize="sentences"
                />
                {flag.status === 'open' && (
                  <Button label="Uphold" kind="danger" busy={busy === flag.id} onPress={() => void decideFlag(flag, 'uphold')} />
                )}
                <Button label="Clear" kind="secondary" busy={busy === flag.id} onPress={() => void decideFlag(flag, 'clear')} />
              </>
            ) : null}
          </View>
        );
      })}
      {cursor && <Button label="Show more" kind="secondary" onPress={() => void load(filter, cursor)} />}
      <RaiseFlagForm onRaised={(flag) => matches(flag, filter) && setFlags((current) => [...(current ?? []), flag])} />
      <AuditLookup />
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

function Choices<T extends string>({
  options,
  value,
  onChange,
  label,
}: {
  options: { value: T; label: string }[];
  value: T | null;
  onChange: (value: T) => void;
  label: string;
}) {
  return (
    <View style={styles.choices} accessibilityRole="radiogroup" accessibilityLabel={label}>
      {options.map((option) => (
        <Pressable
          key={option.value}
          accessibilityRole="radio"
          accessibilityLabel={option.label}
          accessibilityState={{ selected: value === option.value }}
          onPress={() => onChange(option.value)}
          style={[styles.choice, value === option.value && styles.choiceSelected]}
        >
          <Text style={[styles.choiceText, value === option.value && styles.choiceTextSelected]}>{option.label}</Text>
        </Pressable>
      ))}
    </View>
  );
}

function RaiseFlagForm({ onRaised }: { onRaised: (flag: FraudFlag) => void }) {
  const { session } = useSession();
  const [handle, setHandle] = useState('');
  const [reason, setReason] = useState<FlagReason | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState<string>();

  async function raise() {
    if (!reason) return;
    const name = handle.trim().replace(/^@/, '');
    setBusy(true);
    setError(undefined);
    setDone(undefined);
    try {
      const flag = unwrap(
        await session.api.POST('/admin/fraud-flags', { body: { handle: name, reason, ...(note.trim() ? { note: note.trim() } : {}) } }),
      );
      setDone(`Flagged @${name}. They're on hold for fan tickets until the flag is cleared.`);
      setHandle('');
      setReason(null);
      setNote('');
      onRaised(flag);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.card}>
      <Text style={styles.title}>Flag a fan</Text>
      <Field label="Fan's handle" value={handle} onChangeText={setHandle} maxLength={40} />
      <Choices options={FLAG_REASONS.map((r) => ({ value: r, label: reasonLabel(r) }))} value={reason} onChange={setReason} label="Reason" />
      <Field label="Flag note (staff only, optional)" value={note} onChangeText={setNote} maxLength={2000} multiline autoCapitalize="sentences" />
      <ErrorText>{error}</ErrorText>
      {done && <Body muted>{done}</Body>}
      <Button label="Flag fan" kind="danger" busy={busy} disabled={!reason || !handle.trim().replace(/^@/, '')} onPress={() => void raise()} />
    </View>
  );
}

function AuditLookup() {
  const { session } = useSession();
  const [handle, setHandle] = useState('');
  const [looked, setLooked] = useState<string | null>(null);
  const [entries, setEntries] = useState<AuditEntry[] | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function lookUp(name: string, after?: string) {
    setBusy(true);
    setError(undefined);
    try {
      const page = unwrap(
        await session.api.GET('/admin/eligibility-audit', {
          params: { query: { handle: name, limit: 50, ...(after ? { cursor: after } : {}) } },
        }),
      );
      setLooked(name);
      setEntries((current) => (after ? [...(current ?? []), ...page.items] : page.items));
      setCursor(page.next_cursor);
    } catch (e) {
      setError(errorMessage(e));
      if (!after) setEntries(null);
    } finally {
      setBusy(false);
    }
  }

  const name = handle.trim().replace(/^@/, '');

  return (
    <View style={styles.card}>
      <Text style={styles.title}>Eligibility audit</Text>
      <Field label="Audit handle" value={handle} onChangeText={setHandle} maxLength={40} />
      <Button label="Look up" kind="secondary" busy={busy} disabled={!name} onPress={() => void lookUp(name)} />
      <ErrorText>{error}</ErrorText>
      {entries?.length === 0 && <Body muted>No audit entries for @{looked}.</Body>}
      {entries?.map((entry) => (
        <View key={entry.id} style={styles.section}>
          <Text style={styles.label}>
            {AUDIT_ACTIONS[entry.action] ?? entry.action}
            {entry.reason ? ` · ${entry.reason}` : ''}
          </Text>
          <Text style={styles.meta}>
            {dateTimeLabel(entry.at)} · {entry.actor ? `@${entry.actor}` : 'system'}
            {entry.allocation_id ? ` · allocation ${entry.allocation_id}` : ''}
          </Text>
          {Object.keys(entry.inputs).length > 0 && <Text style={styles.code}>{prettyJson(entry.inputs)}</Text>}
        </View>
      ))}
      {cursor && looked && <Button label="More audit entries" kind="secondary" busy={busy} onPress={() => void lookUp(looked, cursor)} />}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  section: { gap: space.xs, paddingTop: space.sm, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.border },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  meta: { color: colors.muted, fontSize: 14 },
  text: { color: colors.text, fontSize: 15, lineHeight: 21 },
  code: { color: colors.text, fontSize: 13, fontFamily: 'monospace', backgroundColor: colors.surface, padding: space.sm, borderRadius: 8 },
  choices: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  choice: { borderWidth: 1, borderColor: colors.border, borderRadius: 999, paddingVertical: space.xs, paddingHorizontal: space.md },
  choiceSelected: { backgroundColor: colors.accent, borderColor: colors.accent },
  choiceText: { color: colors.text, fontSize: 14 },
  choiceTextSelected: { color: colors.onAccent, fontWeight: '600' },
});
