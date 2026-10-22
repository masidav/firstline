import * as ImagePicker from 'expo-image-picker';
import { router } from 'expo-router';
import { useEffect, useState } from 'react';
import { Image, Pressable, StyleSheet, Text, View } from 'react-native';

import { ApiError } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Body, Button, ErrorText, Field, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { POST_KINDS, postPhoto, preparePhoto, type SubmissionKind } from './photoUpload';

type ArtistChoice = { id: string; name: string };

/**
 * Post a concert photo, merch, or ticket (route /post). It goes to staff as proof of fandom; once verified it earns
 * points, and a concert or merch photo can then be shared to the home feed from My posts.
 */
export function PostPhotoScreen() {
  const { session } = useSession();
  const [kind, setKind] = useState<SubmissionKind>('concert_photo');
  const [mine, setMine] = useState<ArtistChoice[]>([]);
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<ArtistChoice[]>([]);
  const [artist, setArtist] = useState<ArtistChoice | null>(null);
  const [photo, setPhoto] = useState<{ uri: string; width?: number } | null>(null);
  const [caption, setCaption] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string>();
  const [sent, setSent] = useState(false);

  // Your artists first: the ones you have scores for.
  useEffect(() => {
    void session.api.GET('/me/scores', { params: { query: { limit: 20 } } }).then((result) => {
      if (result.data) setMine(result.data.items.map((s) => ({ id: s.artist.id, name: s.artist.name })));
    });
  }, [session]);

  useEffect(() => {
    const text = query.trim();
    if (!text) {
      setFound([]);
      return;
    }
    const timer = setTimeout(() => {
      void session.api
        .GET('/artists', { params: { query: { q: text, status: 'all', sort: 'name', limit: 8 } } })
        .then((result) => setFound(result.data?.items.map((a) => ({ id: a.id, name: a.name })) ?? []));
    }, 300);
    return () => clearTimeout(timer);
  }, [query, session]);

  async function pick() {
    setError(undefined);
    const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'], quality: 1 });
    const asset = result.canceled ? undefined : result.assets[0];
    if (asset) setPhoto({ uri: asset.uri, width: asset.width });
  }

  async function send() {
    if (!artist || !photo) return;
    setSending(true);
    setError(undefined);
    try {
      const prepared = await preparePhoto(photo.uri, photo.width);
      await postPhoto(session.api, { artistId: artist.id, kind, caption, photoUri: prepared });
      setSent(true);
    } catch (e) {
      // Upload problems we explain ourselves are plain Errors; API errors and network failures use the usual wording.
      setError(e instanceof Error && !(e instanceof ApiError) && e.name === 'Error' ? e.message : errorMessage(e));
    } finally {
      setSending(false);
    }
  }

  if (sent) {
    return (
      <Screen>
        <Title>Sent for review</Title>
        <Body>
          Staff check every post. Once it's verified you earn points
          {kind === 'ticket' ? '.' : ', and you can share it to the home feed from My posts.'}
        </Body>
        <Button label="My posts" onPress={() => router.replace('/posts')} />
        <Button label="Back to home" kind="secondary" onPress={() => router.replace('/home')} />
      </Screen>
    );
  }

  const choices = query.trim() ? found : mine;
  return (
    <Screen>
      <Title>Post a photo</Title>
      <Body muted>Proof of fandom: staff check it, and verified posts earn points toward your score.</Body>

      <Text style={styles.label}>What is it?</Text>
      <View style={styles.choices} accessibilityRole="radiogroup">
        {POST_KINDS.map((option) => (
          <Choice key={option.kind} label={option.label} selected={kind === option.kind} onPress={() => setKind(option.kind)} />
        ))}
      </View>
      <Body muted>{POST_KINDS.find((option) => option.kind === kind)?.hint}</Body>

      <Text style={styles.label}>Artist</Text>
      {artist ? (
        <View style={styles.selected}>
          <Text style={styles.selectedName}>{artist.name}</Text>
          <Button label="Change" kind="secondary" onPress={() => setArtist(null)} />
        </View>
      ) : (
        <>
          <Field label="Find an artist" placeholder="Artist name" value={query} onChangeText={setQuery} autoCapitalize="words" />
          <View style={styles.choices}>
            {choices.map((choice) => (
              <Choice key={choice.id} label={choice.name} selected={false} onPress={() => setArtist(choice)} />
            ))}
          </View>
          {query.trim() !== '' && found.length === 0 && <Body muted>No tracked artist matches. Add them from home first.</Body>}
        </>
      )}

      <Text style={styles.label}>Photo</Text>
      {photo && <Image source={{ uri: photo.uri }} style={styles.preview} resizeMode="cover" accessibilityLabel="Chosen photo" />}
      <Button label={photo ? 'Choose another photo' : 'Choose a photo'} kind="secondary" onPress={() => void pick()} />

      {kind !== 'ticket' && (
        <Field
          label="Caption (optional)"
          placeholder="Shown if you share it"
          value={caption}
          onChangeText={setCaption}
          maxLength={500}
          autoCapitalize="sentences"
        />
      )}

      <ErrorText>{error}</ErrorText>
      <Button label="Send for review" onPress={() => void send()} busy={sending} disabled={!artist || !photo} />
      <Button label="Cancel" kind="secondary" onPress={() => router.back()} disabled={sending} />
    </Screen>
  );
}

function Choice({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ selected }}
      accessibilityLabel={label}
      onPress={onPress}
      style={[styles.choice, selected && styles.choiceSelected]}
    >
      <Text style={[styles.choiceText, selected && styles.choiceTextSelected]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  label: { color: colors.text, fontSize: 14, fontWeight: '600', marginTop: space.sm },
  choices: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm },
  choice: { borderWidth: 1, borderColor: colors.border, borderRadius: 999, paddingVertical: space.sm, paddingHorizontal: space.md },
  choiceSelected: { backgroundColor: colors.accent, borderColor: colors.accent },
  choiceText: { color: colors.text, fontSize: 15 },
  choiceTextSelected: { color: colors.onAccent, fontWeight: '600' },
  selected: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: space.md },
  selectedName: { color: colors.text, fontSize: 16, fontWeight: '600', flex: 1 },
  preview: { width: '100%', aspectRatio: 4 / 3, borderRadius: 12, backgroundColor: colors.surface },
});
