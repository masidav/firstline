import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { formatPrice, standingMessage } from './standing';

type EventDetail = components['schemas']['EventDetail'];
type Allocation = components['schemas']['Allocation'];
type Eligibility = components['schemas']['Eligibility'];

const reasons: Record<string, string> = {
  not_open: "Registration isn't open right now.",
  artist_not_active: "This artist isn't active right now, so registration is paused.",
  hub_city_required: 'Choose your hub cities before registering.',
  no_score: "You don't have a fandom score for this artist yet. Connect Spotify and listen to them.",
  below_threshold: "Your fandom score for this artist is below this allocation's minimum.",
  already_registered: "You're registered for another allocation of this show, or you're someone's +1 for it.",
};

/** A show and its fan allocations (GET /events/{id}), with registration for each. */
export function ShowScreen({ eventId }: { eventId: string }) {
  const { session } = useSession();
  const [detail, setDetail] = useState<EventDetail | null>(null);
  const [error, setError] = useState<string>();

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setDetail(unwrap(await session.api.GET('/events/{eventId}', { params: { path: { eventId } } })));
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session, eventId]);

  useEffect(() => {
    void load();
  }, [load]);

  const show = detail?.event;
  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      {error && (
        <>
          <ErrorText>{error}</ErrorText>
          <Button label="Try again" kind="secondary" onPress={() => void load()} />
        </>
      )}
      {!detail && !error && <Loading />}
      {show && (
        <>
          <Title>{show.name}</Title>
          <Body muted>
            {new Date(show.starts_at).toLocaleString()} · {[show.venue, show.city].filter(Boolean).join(', ')}
          </Body>
          {detail.allocations.length === 0 && <Body muted>No fan tickets for this show yet.</Body>}
          {detail.allocations.map((allocation) => (
            <AllocationCard key={allocation.id} allocation={allocation} />
          ))}
        </>
      )}
    </Screen>
  );
}

function AllocationCard({ allocation }: { allocation: Allocation }) {
  const { session } = useSession();
  const [eligibility, setEligibility] = useState<Eligibility | null>(null);
  // For a fan who hasn't registered yet: whether they want a +1 (the standing counts it).
  const [plusOne, setPlusOne] = useState(false);
  const [guest, setGuest] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const id = allocation.id;

  const load = useCallback(
    async (withPlusOne: boolean) => {
      try {
        setEligibility(
          unwrap(
            await session.api.GET('/allocations/{allocationId}/eligibility', {
              params: { path: { allocationId: id }, query: { plus_one: withPlusOne } },
            }),
          ),
        );
      } catch (e) {
        setError(errorMessage(e));
      }
    },
    [session, id],
  );

  useEffect(() => {
    if (allocation.status !== 'cancelled') void load(false);
  }, [load, allocation.status]);

  const act = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      await load(plusOne);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const registration = eligibility?.registration ?? null;
  const wantsPlusOne = registration ? registration.plus_one : plusOne;
  const message = eligibility?.standing ? standingMessage(eligibility.standing) : null;
  const canChangePlusOne =
    allocation.status === 'open' ? !registration?.guest : registration?.status === 'selected' && registration.seats === 1;
  const canNameGuest =
    registration?.plus_one && !registration.guest && (registration.status === 'registered' || registration.seats === 2);

  const togglePlusOne = () =>
    registration
      ? act(async () => {
          unwrap(
            await session.api.PUT('/allocations/{allocationId}/registration', {
              params: { path: { allocationId: id } },
              body: { plus_one: !registration.plus_one },
            }),
          );
        })
      : (setPlusOne(!plusOne), void load(!plusOne));

  return (
    <View style={styles.card}>
      <Text style={styles.name}>{allocation.name}</Text>
      <Body muted>
        {formatPrice(allocation.face_value_cents, allocation.currency)} · {allocation.tickets} tickets · registration
        closes {new Date(allocation.registration_closes_at).toLocaleString()}
      </Body>

      {allocation.status === 'cancelled' && <Body>This allocation was cancelled.</Body>}
      {allocation.status === 'upcoming' && (
        <Body>Registration opens {new Date(allocation.registration_opens_at).toLocaleString()}.</Body>
      )}

      {registration?.status === 'selected' && (
        <Text style={styles.good}>
          {registration.seats === 2 ? 'You got tickets for you and your +1!' : 'You got a ticket!'}
          {registration.plus_one && registration.seats === 1 ? " There wasn't a seat left for your +1." : ''}
        </Text>
      )}
      {registration?.status === 'not_selected' && <Body>You weren't selected this time.</Body>}
      {registration?.status === 'registered' && <Text style={styles.good}>You're registered.</Text>}

      {message && (
        <View accessibilityRole="summary" style={[styles.standing, eligibility?.standing?.likely && styles.likely]}>
          <Text style={styles.headline}>{message.headline}</Text>
          <Body muted>{message.detail}</Body>
        </View>
      )}
      {eligibility && !eligibility.eligible && eligibility.reason && !registration && allocation.status === 'open' && (
        <Body muted>{reasons[eligibility.reason] ?? eligibility.reason}</Body>
      )}

      {registration?.guest && <Body muted>Your +1: @{registration.guest.handle}</Body>}
      {canChangePlusOne && (eligibility?.eligible || registration) && (
        <Button
          label={wantsPlusOne ? 'Bringing a +1 (tap to remove)' : 'Bring a +1'}
          kind="secondary"
          busy={busy}
          onPress={() => void togglePlusOne()}
        />
      )}
      {!registration && eligibility?.eligible && allocation.status === 'open' && (
        <Button
          label="Register"
          busy={busy}
          onPress={() =>
            void act(async () => {
              unwrap(
                await session.api.PUT('/allocations/{allocationId}/registration', {
                  params: { path: { allocationId: id } },
                  body: { plus_one: plusOne },
                }),
              );
            })
          }
        />
      )}
      {canNameGuest && (
        <>
          <Field label="Your +1's handle" value={guest} onChangeText={setGuest} placeholder="Their handle (they need an account)" />
          <Button
            label="Name my +1"
            kind="secondary"
            busy={busy}
            disabled={guest.trim().length === 0}
            onPress={() =>
              void act(async () => {
                unwrap(
                  await session.api.PUT('/allocations/{allocationId}/registration/guest', {
                    params: { path: { allocationId: id } },
                    body: { handle: guest.trim() },
                  }),
                );
              })
            }
          />
        </>
      )}
      {registration?.status === 'registered' && allocation.status === 'open' && (
        <Button
          label="Withdraw"
          kind="danger"
          busy={busy}
          onPress={() =>
            void act(async () => {
              unwrap(
                await session.api.DELETE('/allocations/{allocationId}/registration', { params: { path: { allocationId: id } } }),
              );
            })
          }
        />
      )}
      <ErrorText>{error}</ErrorText>
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  name: { color: colors.text, fontSize: 18, fontWeight: '700' },
  good: { color: colors.accent, fontSize: 16, fontWeight: '600' },
  standing: { gap: space.xs, padding: space.sm, borderRadius: 8, backgroundColor: colors.surface },
  likely: { borderWidth: 1, borderColor: colors.accent },
  headline: { color: colors.text, fontSize: 16, fontWeight: '700' },
});
