import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { formatWhen } from '../feed/feedText';
import { formatPrice } from './standing';

export type Reservation = components['schemas']['Reservation'];

/** The one-line state of a reservation as the fan reads it. */
export function reservationLine(reservation: Reservation): string {
  switch (reservation.status) {
    case 'held':
      return `Your seat${reservation.seats === 2 ? 's are' : ' is'} held. The ticket arrives ${formatWhen(reservation.deliver_at)}.`;
    case 'delivered':
      return reservation.role === 'holder'
        ? 'Your ticket is here. Show it at the door.'
        : `You're @${reservation.holder_handle}'s +1. They show the ticket at the door.`;
    case 'returned':
      return `Returned ${reservation.returned_at ? formatWhen(reservation.returned_at) : ''}. The seat${reservation.seats === 2 ? 's' : ''} went to the next fan in line.`;
    default:
      return reservation.cancelled_reason === 'show_cancelled' ? 'The show was cancelled.' : 'This reservation was cancelled.';
  }
}

/**
 * Your tickets (GET /me/reservations): the seats you hold from the draws, and the ones you are the named +1 of.
 * A ticket is non-transferable and arrives shortly before the show (the allocation's delivery rule); until then the
 * reservation says when. A holder who can't go returns it (POST /reservations/{id}/return) and the seats go to
 * the next fan in the ranking at face value.
 */
export function MyTicketsScreen() {
  const { session } = useSession();
  const [items, setItems] = useState<Reservation[] | null>(null);
  const [error, setError] = useState<string>();
  const [confirming, setConfirming] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setItems(unwrap(await session.api.GET('/me/reservations')).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function giveBack(reservation: Reservation) {
    setBusy(reservation.id);
    setError(undefined);
    try {
      const returned = unwrap(
        await session.api.POST('/reservations/{reservationId}/return', { params: { path: { reservationId: reservation.id } } }),
      );
      setItems((current) => current && current.map((r) => (r.id === returned.id ? returned : r)));
      setConfirming(null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <Screen>
      <Button label="Back" kind="secondary" onPress={() => (router.canGoBack() ? router.back() : router.replace('/home'))} />
      <Title>Your tickets</Title>
      <Body muted>
        Face-value seats from the fan draws. Tickets are tied to your account and arrive shortly before the show. Can't
        go? Return it and the seat goes to the next fan in line.
      </Body>
      <ErrorText>{error}</ErrorText>
      {error && <Button label="Try again" kind="secondary" onPress={() => void load()} />}
      {!items && !error && <Loading />}
      {items?.length === 0 && <Body muted>No tickets yet. Register for a show's fan allocation and win the draw.</Body>}
      {items?.map((reservation) => {
        const where = [reservation.event.venue, reservation.event.city].filter(Boolean).join(', ');
        const live = reservation.status === 'held' || reservation.status === 'delivered';
        return (
          <View key={reservation.id} style={[styles.card, live && styles.live]}>
            <Text style={styles.kicker}>
              {reservation.role === 'guest' ? `@${reservation.holder_handle}'s +1 · ` : ''}
              {reservation.seats === 2 ? '2 seats' : '1 seat'} · {formatPrice(reservation.face_value_cents, reservation.currency)} each
            </Text>
            <Text style={styles.title}>{reservation.event.name}</Text>
            <Text style={styles.muted}>
              {reservation.artist.name} · {formatWhen(reservation.event.starts_at)}
              {where ? ` · ${where}` : ''}
            </Text>
            <Text style={[styles.line, reservation.status === 'delivered' && styles.good]}>{reservationLine(reservation)}</Text>
            {reservation.ticket && (
              <View style={styles.ticket} accessibilityLabel="Your ticket">
                <Text style={styles.ticketLabel}>Ticket · @{reservation.ticket.holder_handle}
                  {reservation.ticket.guest_handle ? ` + @${reservation.ticket.guest_handle}` : ''}</Text>
                <Text style={styles.token} selectable>
                  {reservation.ticket.token.replace(/(.{8})/g, '$1 ').trim()}
                </Text>
                <Text style={styles.muted}>Non-transferable. Bring the account you signed in with.</Text>
              </View>
            )}
            {reservation.guest_handle && reservation.role === 'holder' && !reservation.ticket && (
              <Text style={styles.muted}>Your +1: @{reservation.guest_handle}</Text>
            )}
            {reservation.can_return &&
              (confirming === reservation.id ? (
                <>
                  <Body>Return your seat{reservation.seats === 2 ? 's' : ''}? The next fan in line gets {reservation.seats === 2 ? 'them' : 'it'} at face value. This can't be undone.</Body>
                  <Button label="Yes, return it" kind="danger" busy={busy === reservation.id} onPress={() => void giveBack(reservation)} />
                  <Button label="Keep it" kind="secondary" disabled={busy === reservation.id} onPress={() => setConfirming(null)} />
                </>
              ) : (
                <Button
                  label="Can't go? Return it"
                  accessibilityLabel={`Return your ticket for ${reservation.event.name}`}
                  kind="secondary"
                  onPress={() => setConfirming(reservation.id)}
                />
              ))}
            <Button label="Show details" kind="secondary" onPress={() => router.push(`/show/${reservation.event.id}`)} />
          </View>
        );
      })}
    </Screen>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  live: { borderColor: colors.accent },
  kicker: { color: colors.muted, fontSize: 13, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },
  title: { color: colors.text, fontSize: 18, fontWeight: '700' },
  muted: { color: colors.muted, fontSize: 14 },
  line: { color: colors.text, fontSize: 15 },
  good: { color: colors.accent, fontWeight: '600' },
  ticket: { gap: space.xs, padding: space.md, borderRadius: 8, backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.accent },
  ticketLabel: { color: colors.text, fontSize: 14, fontWeight: '700' },
  token: { color: colors.text, fontSize: 16, fontFamily: 'monospace', lineHeight: 24 },
});
