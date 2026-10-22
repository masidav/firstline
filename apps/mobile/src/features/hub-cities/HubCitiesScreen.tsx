import { router } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { ApiError, type components, type Me, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';

export type HubCity = components['schemas']['HubCity'];

const MAX_CITIES = 3;
const DEBOUNCE_MS = 250;

/** Back to the profile this screen was opened from, without stacking a second profile. */
function backToProfile() {
  if (router.canGoBack()) router.back();
  else router.replace('/profile');
}

export function cityLabel(city: HubCity): string {
  return `${city.name}, ${city.region}`;
}

/** "Wednesday, October 28": when the next change is allowed. */
export function formatDay(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
}

/**
 * Choose a primary hub city and up to two backups (PUT /me/hub-cities). New fans land here right after sign-up and
 * can't continue without one. Shows in hub cities put the fan first in line. After the first choice, hub cities can
 * change once a week, so changing asks for confirmation first.
 */
export function HubCitiesScreen({ me }: { me: Me }) {
  const { session } = useSession();
  const firstTime = me.hub_cities.length === 0;
  const lockedUntil = me.hub_cities_change_available_at ?? null;
  const [chosen, setChosen] = useState<HubCity[]>(me.hub_cities);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<HubCity[]>([]);
  const [searchError, setSearchError] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  // Only the newest search may show its results.
  const latest = useRef(0);

  useEffect(() => {
    const text = query.trim();
    if (!text) {
      latest.current++;
      setResults([]);
      return;
    }
    const timer = setTimeout(async () => {
      const request = ++latest.current;
      try {
        const found = unwrap(await session.api.GET('/cities', { params: { query: { q: text, limit: 20 } } }));
        if (request === latest.current) {
          setResults(found.items);
          setSearchError(undefined);
        }
      } catch (e) {
        if (request === latest.current) setSearchError(errorMessage(e));
      }
    }, DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [query, session]);

  const unchanged = chosen.map((c) => c.id).join() === me.hub_cities.map((c) => c.id).join();

  function add(city: HubCity) {
    setChosen((current) => [...current, city]);
    setQuery('');
    setConfirming(false);
  }

  function remove(city: HubCity) {
    setChosen((current) => current.filter((c) => c.id !== city.id));
    setConfirming(false);
  }

  function makePrimary(city: HubCity) {
    setChosen((current) => [city, ...current.filter((c) => c.id !== city.id)]);
    setConfirming(false);
  }

  async function save() {
    setSaving(true);
    setError(undefined);
    try {
      const updated = unwrap(await session.api.PUT('/me/hub-cities', { body: { city_ids: chosen.map((c) => c.id) } }));
      session.setMe(updated);
      if (firstTime) router.replace('/home');
      else backToProfile();
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'hub_change_limit' ? e.message : errorMessage(e));
      setSaving(false);
    }
  }

  if (lockedUntil) {
    return (
      <Screen>
        <Title>Your hub cities</Title>
        <ChosenList cities={me.hub_cities} />
        <Body muted>You changed your hub cities recently. You can change them again on {formatDay(lockedUntil)}.</Body>
        <Button label="Back to profile" kind="secondary" onPress={backToProfile} />
      </Screen>
    );
  }

  const nextWeek = formatDay(new Date(Date.now() + 7 * 24 * 3600 * 1000).toISOString());
  return (
    <Screen>
      <Title>{firstTime ? 'Pick your hub city' : 'Your hub cities'}</Title>
      <Body muted>
        Fans in a show's city get first pick of tickets. Choose the city you go to shows in, plus up to two backups.
      </Body>

      <ChosenList cities={chosen} onRemove={remove} onMakePrimary={makePrimary} />

      {chosen.length < MAX_CITIES ? (
        <>
          <Field
            label={chosen.length === 0 ? 'Search for your city' : 'Add a backup city'}
            placeholder="City name"
            value={query}
            onChangeText={setQuery}
            autoCapitalize="words"
            maxLength={100}
          />
          <ErrorText>{searchError}</ErrorText>
          {query.trim() !== '' && results.length === 0 && !searchError && (
            <Body muted>No city in our list matches "{query.trim()}". We cover major US and Canadian cities.</Body>
          )}
          {results.map((city) => {
            const already = chosen.some((c) => c.id === city.id);
            return (
              <View key={city.id} style={styles.row}>
                <Text style={styles.name}>{cityLabel(city)}</Text>
                <View style={styles.action}>
                  <Button
                    label={already ? 'Chosen' : 'Add'}
                    accessibilityLabel={`Add ${cityLabel(city)}`}
                    kind="secondary"
                    disabled={already}
                    onPress={() => add(city)}
                  />
                </View>
              </View>
            );
          })}
        </>
      ) : (
        <Body muted>That's three: a primary city and two backups.</Body>
      )}

      <ErrorText>{error}</ErrorText>
      {firstTime ? (
        <Button label="Continue" onPress={() => void save()} busy={saving} disabled={chosen.length === 0} />
      ) : confirming ? (
        <>
          <Body>
            You can change your hub cities once a week. After saving, your next change is possible on {nextWeek}.
            Registrations you've already made keep the cities they were made with.
          </Body>
          <Button label="Save hub cities" onPress={() => void save()} busy={saving} />
          <Button label="Keep editing" kind="secondary" onPress={() => setConfirming(false)} disabled={saving} />
        </>
      ) : (
        <>
          <Button label="Save" onPress={() => setConfirming(true)} disabled={chosen.length === 0 || unchanged} />
          <Button label="Back to profile" kind="secondary" onPress={backToProfile} />
        </>
      )}
    </Screen>
  );
}

function ChosenList({
  cities,
  onRemove,
  onMakePrimary,
}: {
  cities: HubCity[];
  onRemove?: (city: HubCity) => void;
  onMakePrimary?: (city: HubCity) => void;
}) {
  return (
    <>
      {cities.map((city, i) => (
        <View key={city.id} style={styles.row}>
          <View style={styles.text}>
            <Text style={styles.name}>{cityLabel(city)}</Text>
            <Text style={styles.note}>{i === 0 ? 'Primary' : 'Backup'}</Text>
          </View>
          {onMakePrimary && i > 0 && (
            <View style={styles.action}>
              <Button
                label="Make primary"
                accessibilityLabel={`Make ${cityLabel(city)} primary`}
                kind="secondary"
                onPress={() => onMakePrimary(city)}
              />
            </View>
          )}
          {onRemove && (
            <View style={styles.action}>
              <Button
                label="Remove"
                accessibilityLabel={`Remove ${cityLabel(city)}`}
                kind="secondary"
                onPress={() => onRemove(city)}
              />
            </View>
          )}
        </View>
      ))}
    </>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  text: { flex: 1, gap: 2 },
  name: { flex: 1, color: colors.text, fontSize: 16, fontWeight: '600' },
  note: { color: colors.muted, fontSize: 14 },
  action: { minWidth: 88 },
});
