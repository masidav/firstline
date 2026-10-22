import { router } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';

import { ApiError, type Me, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { cityLabel, formatDay } from '../hub-cities/HubCitiesScreen';

export function ProfileScreen({ me }: { me: Me }) {
  const { session } = useSession();
  const [handle, setHandle] = useState(me.handle);
  const [displayName, setDisplayName] = useState(me.display_name ?? '');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [errors, setErrors] = useState<{ handle?: string; displayName?: string; form?: string }>({});
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string>();

  const changed = handle !== me.handle || (displayName.trim() || null) !== (me.display_name ?? null);

  async function save() {
    setSaving(true);
    setSaved(false);
    setErrors({});
    try {
      const body: { handle?: string; display_name?: string | null } = {};
      if (handle !== me.handle) body.handle = handle.trim();
      if ((displayName.trim() || null) !== (me.display_name ?? null)) body.display_name = displayName.trim() || null;
      session.setMe(unwrap(await session.api.PATCH('/me', { body })));
      setSaved(true);
    } catch (e) {
      setErrors(
        e instanceof ApiError
          ? {
              handle: e.code === 'handle_taken' ? errorMessage(e) : e.fieldError('handle'),
              displayName: e.fieldError('display_name'),
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
      <Button label="Your stats" kind="secondary" onPress={() => router.push('/stats')} />

      <Field
        label="Handle"
        value={handle}
        onChangeText={(value) => {
          setHandle(value);
          setSaved(false);
        }}
        error={errors.handle}
      />
      <Field
        label="Display name"
        value={displayName}
        onChangeText={(value) => {
          setDisplayName(value);
          setSaved(false);
        }}
        placeholder="Optional"
        autoCapitalize="words"
        maxLength={60}
        error={errors.displayName}
      />
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
