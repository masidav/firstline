import { router } from 'expo-router';
import { useState } from 'react';
import { Linking, Pressable, StyleSheet, Switch, Text, View } from 'react-native';

import { ApiError, type Me, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { cityLabel, formatDay } from '../hub-cities/HubCitiesScreen';

type Errors = {
  handle?: string;
  displayName?: string;
  isArtist?: string;
  bandUrl?: string;
  instagram?: string;
  x?: string;
  form?: string;
};

/** The band's links as shown on the page. Handles are stored bare; the links are built here. */
export function bandLinks(me: Pick<Me, 'is_artist' | 'band_url' | 'instagram_handle' | 'x_handle'>) {
  if (!me.is_artist) return [];
  const links: { label: string; url: string }[] = [];
  if (me.band_url) links.push({ label: 'Band profile', url: me.band_url });
  if (me.instagram_handle) links.push({ label: `Instagram @${me.instagram_handle}`, url: `https://instagram.com/${me.instagram_handle}` });
  if (me.x_handle) links.push({ label: `X @${me.x_handle}`, url: `https://x.com/${me.x_handle}` });
  return links;
}

const bare = (handle: string) => handle.trim().replace(/^@/, '') || null;

export function ProfileScreen({ me }: { me: Me }) {
  const { session } = useSession();
  const [handle, setHandle] = useState(me.handle);
  const [displayName, setDisplayName] = useState(me.display_name ?? '');
  const [isArtist, setIsArtist] = useState(me.is_artist);
  const [bandUrl, setBandUrl] = useState(me.band_url ?? '');
  const [instagram, setInstagram] = useState(me.instagram_handle ?? '');
  const [x, setX] = useState(me.x_handle ?? '');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string>();

  // What the save would send. Band fields only count while the artist switch is on: turning it off clears them.
  const body: {
    handle?: string;
    display_name?: string | null;
    is_artist?: boolean;
    band_url?: string | null;
    instagram_handle?: string | null;
    x_handle?: string | null;
  } = {};
  if (handle !== me.handle) body.handle = handle.trim();
  if ((displayName.trim() || null) !== (me.display_name ?? null)) body.display_name = displayName.trim() || null;
  if (isArtist !== me.is_artist) body.is_artist = isArtist;
  if (isArtist) {
    if ((bandUrl.trim() || null) !== (me.band_url ?? null)) body.band_url = bandUrl.trim() || null;
    if (bare(instagram) !== (me.instagram_handle ?? null)) body.instagram_handle = bare(instagram);
    if (bare(x) !== (me.x_handle ?? null)) body.x_handle = bare(x);
  }
  const changed = Object.keys(body).length > 0;
  const links = bandLinks(me);

  function edit<T>(set: (value: T) => void) {
    return (value: T) => {
      set(value);
      setSaved(false);
    };
  }

  async function save() {
    setSaving(true);
    setSaved(false);
    setErrors({});
    try {
      const next = unwrap(await session.api.PATCH('/me', { body }));
      session.setMe(next);
      setBandUrl(next.band_url ?? '');
      setInstagram(next.instagram_handle ?? '');
      setX(next.x_handle ?? '');
      setSaved(true);
    } catch (e) {
      setErrors(
        e instanceof ApiError
          ? {
              handle: e.code === 'handle_taken' ? errorMessage(e) : e.fieldError('handle'),
              displayName: e.fieldError('display_name'),
              isArtist: e.fieldError('is_artist'),
              bandUrl: e.fieldError('band_url'),
              instagram: e.fieldError('instagram_handle'),
              x: e.fieldError('x_handle'),
              form: e.problem.errors ? undefined : errorMessage(e),
            }
          : { form: errorMessage(e) },
      );
    } finally {
      setSaving(false);
    }
  }

  async function deleteAccount() {
    setDeleting(true);
    setDeleteError(undefined);
    try {
      // On iOS this will first get a fresh Sign in with Apple code for Apple accounts (App Store rule).
      await session.deleteAccount();
    } catch (e) {
      setDeleteError(errorMessage(e));
      setDeleting(false);
    }
  }

  return (
    <Screen>
      <Button label="Back to home" kind="secondary" onPress={() => router.replace('/home')} />
      <Title>Your profile</Title>
      <Body muted>Signed in as {me.email}</Body>
      {links.length > 0 && (
        <View style={styles.links}>
          <Body>Your band</Body>
          {links.map((link) => (
            <Pressable
              key={link.url}
              accessibilityRole="link"
              accessibilityLabel={`Open ${link.label}`}
              onPress={() => void Linking.openURL(link.url)}
            >
              <Text style={styles.link}>{link.label}</Text>
            </Pressable>
          ))}
        </View>
      )}
      <Button label="Your stats" kind="secondary" onPress={() => router.push('/stats')} />

      <Field label="Handle" value={handle} onChangeText={edit(setHandle)} error={errors.handle} />
      <Field
        label="Display name"
        value={displayName}
        onChangeText={edit(setDisplayName)}
        placeholder="Optional"
        autoCapitalize="words"
        maxLength={60}
        error={errors.displayName}
      />

      <View style={styles.switchRow}>
        <Text style={styles.switchLabel}>I'm an artist</Text>
        <Switch accessibilityLabel="I'm an artist" value={isArtist} onValueChange={edit(setIsArtist)} />
      </View>
      <ErrorText>{errors.isArtist}</ErrorText>
      {isArtist ? (
        <>
          <Body muted>Your band's links show on your page.</Body>
          <Field
            label="Band profile link"
            value={bandUrl}
            onChangeText={edit(setBandUrl)}
            placeholder="https://"
            keyboardType="url"
            maxLength={200}
            error={errors.bandUrl}
          />
          <Field label="Instagram" value={instagram} onChangeText={edit(setInstagram)} placeholder="@yourband" maxLength={31} error={errors.instagram} />
          <Field label="X" value={x} onChangeText={edit(setX)} placeholder="@yourband" maxLength={16} error={errors.x} />
        </>
      ) : (
        me.is_artist && <Body muted>Turning this off removes your band's links.</Body>
      )}

      <ErrorText>{errors.form}</ErrorText>
      {saved && <Body muted>Saved.</Body>}
      <Button label="Save" onPress={() => void save()} busy={saving} disabled={!changed} />

      <View style={{ height: 24 }} />
      <Body>{me.spotify?.connected ? 'Spotify: connected' : 'Spotify: not connected'}</Body>
      <Button
        label={me.spotify?.connected ? 'Spotify settings' : 'Connect Spotify'}
        kind="secondary"
        onPress={() => router.push('/spotify')}
      />

      <View style={{ height: 24 }} />
      <Body>
        {me.hub_cities.length > 0
          ? `Hub cities: ${me.hub_cities.map((city, i) => `${cityLabel(city)}${i === 0 ? ' (primary)' : ''}`).join(' · ')}`
          : 'No hub city yet'}
      </Body>
      {me.hub_cities_change_available_at && (
        <Body muted>You can change them again on {formatDay(me.hub_cities_change_available_at)}.</Body>
      )}
      <Button label="Change hub cities" kind="secondary" onPress={() => router.push('/hub-cities')} />

      <View style={{ height: 24 }} />
      <Button label="Sign out" kind="secondary" onPress={() => void session.signOut()} />

      <View style={{ height: 24 }} />
      {confirmingDelete ? (
        <>
          <Body>
            Delete your account? You are signed out everywhere and your email and handle are freed at once. Your
            logs, lists, and comments are permanently deleted after 30 days. This can't be undone.
          </Body>
          <ErrorText>{deleteError}</ErrorText>
          <Button label="Yes, delete my account" kind="danger" onPress={() => void deleteAccount()} busy={deleting} />
          <Button label="Keep my account" kind="secondary" onPress={() => setConfirmingDelete(false)} disabled={deleting} />
        </>
      ) : (
        <Button label="Delete account" kind="secondary" onPress={() => setConfirmingDelete(true)} />
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  links: { marginTop: space.sm, gap: space.xs },
  link: { color: colors.accent, fontSize: 16, fontWeight: '600' },
  switchRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: space.md },
  switchLabel: { color: colors.text, fontSize: 16 },
});
