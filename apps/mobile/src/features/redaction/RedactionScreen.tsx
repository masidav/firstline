import { router } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { type GestureResponderEvent, Image, type LayoutChangeEvent, StyleSheet, Text, View } from 'react-native';

import { type components, unwrap } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { useSession } from '../../auth/SessionProvider';
import { Avatar } from '../../ui/Avatar';
import { Body, Button, ErrorText, Loading, Screen, Title } from '../../ui/components';
import { colors, space } from '../../ui/theme';
import { kindLabel } from '../submissions/photoUpload';
import { type Box, boxFromDrag, boxToFrame, containedFrame, MAX_BOXES, type Point, type Size } from './boxes';

type RedactionReview = components['schemas']['RedactionReview'];

/** "in 3 days", "today", or "overdue": when the quarantine bucket deletes the original. */
export function deletesIn(deletedAfter: string, now: Date = new Date()): string {
  const days = Math.floor((new Date(deletedAfter).getTime() - now.getTime()) / 86_400_000);
  if (days < 0) return 'overdue';
  if (days === 0) return 'today';
  return days === 1 ? 'in 1 day' : `in ${days} days`;
}

/**
 * Staff only (route /redactions): verified proof the redaction worker couldn't settle. Look at the photo: if a
 * barcode, QR code, or order number is visible, draw a box over it (the worker paints the boxes black, still blacks
 * out any code it finds, then archives); if the photo shouldn't be kept at all, discard it.
 */
export function RedactionScreen() {
  const { session } = useSession();
  const [items, setItems] = useState<RedactionReview[] | null>(null);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [boxes, setBoxes] = useState<Record<string, Box[]>>({});

  const load = useCallback(async () => {
    setError(undefined);
    try {
      setItems(unwrap(await session.api.GET('/admin/redactions')).items);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [session]);

  useEffect(() => {
    void load();
  }, [load]);

  async function decide(item: RedactionReview, action: 'archive' | 'discard') {
    setBusy(item.file_id);
    setError(undefined);
    const drawn = boxes[item.file_id] ?? [];
    try {
      unwrap(
        await session.api.POST('/admin/redactions/{fileId}/decision', {
          params: { path: { fileId: item.file_id } },
          body: action === 'archive' && drawn.length > 0 ? { action, boxes: drawn } : { action },
        }),
      );
      setItems((current) => current?.filter((i) => i.file_id !== item.file_id) ?? null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  if (!items && !error) return <Loading />;

  return (
    <Screen>
      <Title>Photo redaction</Title>
      <Body muted>
        Barcodes and QR codes are blacked out before proof is archived. These photos need your eyes. If a barcode, QR
        code, or order number is still visible, draw a box over it and archive: the boxes are blacked out first.
      </Body>
      <ErrorText>{error}</ErrorText>
      {items?.length === 0 && <Body muted>Nothing waiting.</Body>}
      {items?.map((item) => {
        const fan = item.user.deleted ? 'Deleted user' : `@${item.user.handle}`;
        const drawn = boxes[item.file_id] ?? [];
        return (
          <View key={item.file_id} style={styles.card} testID={`redaction-${item.file_id}`}>
            <View style={styles.who}>
              <Avatar user={item.user} size={24} />
              <Text style={[styles.title, styles.whoText]}>
                {kindLabel(item.kind)} · {item.artist.name} · {fan}
              </Text>
            </View>
            <PhotoWithBoxes
              fileId={item.file_id}
              uri={item.file.url}
              boxes={drawn}
              onChange={(next) => setBoxes((current) => ({ ...current, [item.file_id]: next }))}
            />
            {item.note ? <Text style={styles.note}>{item.note}</Text> : null}
            <Text style={styles.meta}>
              {item.codes_redacted > 0 ? `${item.codes_redacted} codes already blacked out. ` : ''}
              Original deleted {deletesIn(item.deleted_after)}.
            </Text>
            <Button
              label={drawn.length > 0 ? `Archive with ${drawn.length} ${drawn.length === 1 ? 'box' : 'boxes'} blacked out` : 'Archive'}
              busy={busy === item.file_id}
              onPress={() => void decide(item, 'archive')}
            />
            <Button label="Discard photo" kind="danger" busy={busy === item.file_id} onPress={() => void decide(item, 'discard')} />
          </View>
        );
      })}
      <Button label="Back to profile" kind="secondary" onPress={() => router.replace('/profile')} />
    </Screen>
  );
}

/**
 * The quarantined original with the boxes staff drew over it. "Draw boxes" turns the photo into a canvas: a drag
 * (mouse or finger) adds a box; the page stops scrolling over the photo until "Done drawing". Boxes are kept as
 * fractions of the image (`boxes.ts`), so what the worker paints matches what was drawn whatever the screen size.
 */
function PhotoWithBoxes({
  fileId,
  uri,
  boxes,
  onChange,
}: {
  fileId: string;
  uri: string;
  boxes: Box[];
  onChange: (boxes: Box[]) => void;
}) {
  const [drawing, setDrawing] = useState(false);
  const [layout, setLayout] = useState<Size>({ width: 0, height: 0 });
  const [imageSize, setImageSize] = useState<Size | null>(null);
  const [drag, setDrag] = useState<{ start: Point; end: Point } | null>(null);

  useEffect(() => {
    let live = true;
    // The image's own size: the canvas takes its aspect ratio so the photo fills it edge to edge (no letterbox).
    Image.getSize(
      uri,
      (width, height) => {
        if (live) setImageSize({ width, height });
      },
      () => {},
    );
    return () => {
      live = false;
    };
  }, [uri]);

  const frame = containedFrame(layout, imageSize);
  const point = (e: GestureResponderEvent): Point => ({ x: e.nativeEvent.locationX, y: e.nativeEvent.locationY });
  const full = boxes.length >= MAX_BOXES;

  function finish(end: Point) {
    if (drag) {
      const box = boxFromDrag(drag.start, end, frame);
      if (box && !full) onChange([...boxes, box]);
    }
    setDrag(null);
  }

  const preview = drag && boxFromDrag(drag.start, drag.end, frame);
  return (
    <View style={styles.photoBlock}>
      <View
        testID={`photo-${fileId}`}
        accessible
        accessibilityLabel={drawing ? 'Photo: drag to draw a box over what should be blacked out' : 'Photo to check'}
        onLayout={(e: LayoutChangeEvent) => setLayout(e.nativeEvent.layout)}
        onStartShouldSetResponder={() => drawing}
        onMoveShouldSetResponder={() => drawing}
        onResponderTerminationRequest={() => false}
        onResponderGrant={(e) => {
          const p = point(e);
          setDrag({ start: p, end: p });
        }}
        onResponderMove={(e) => {
          const p = point(e);
          setDrag((current) => (current ? { start: current.start, end: p } : null));
        }}
        onResponderRelease={(e) => finish(point(e))}
        onResponderTerminate={() => setDrag(null)}
        style={[styles.photo, imageSize ? { aspectRatio: imageSize.width / imageSize.height } : null]}
      >
        {/* The image fills the canvas so a touch on it (native reports locationX from the touched view) is measured
            from the same corner; the boxes let touches through for the same reason. */}
        <Image source={{ uri }} style={StyleSheet.absoluteFill} resizeMode="contain" accessibilityLabel="Photo to check" />
        {boxes.map((box, i) => (
          <View key={i} pointerEvents="none" style={[styles.box, boxToFrame(box, frame)]} testID={`box-${fileId}-${i}`} />
        ))}
        {preview ? <View pointerEvents="none" style={[styles.box, styles.previewBox, boxToFrame(preview, frame)]} /> : null}
      </View>
      <View style={styles.tools}>
        <Button
          label={drawing ? 'Done drawing' : 'Draw boxes'}
          kind="secondary"
          onPress={() => {
            setDrawing(!drawing);
            setDrag(null);
          }}
        />
        {boxes.length > 0 ? (
          <Button label="Remove last box" kind="secondary" onPress={() => onChange(boxes.slice(0, -1))} />
        ) : null}
      </View>
      {drawing ? (
        <Text style={styles.meta}>
          {full
            ? `At most ${MAX_BOXES} boxes per photo.`
            : `Drag over anything to black out. ${boxes.length} ${boxes.length === 1 ? 'box' : 'boxes'} so far.`}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { gap: space.sm, padding: space.md, borderRadius: 12, borderWidth: 1, borderColor: colors.border },
  title: { color: colors.text, fontSize: 16, fontWeight: '600' },
  who: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  whoText: { flex: 1 },
  photoBlock: { gap: space.sm },
  photo: { width: '100%', aspectRatio: 4 / 3, borderRadius: 8, backgroundColor: colors.surface, overflow: 'hidden' },
  box: { position: 'absolute', backgroundColor: 'rgba(0, 0, 0, 0.85)', borderWidth: 1, borderColor: colors.danger },
  previewBox: { backgroundColor: 'rgba(229, 72, 77, 0.35)' },
  tools: { flexDirection: 'row', gap: space.sm, flexWrap: 'wrap' },
  note: { color: colors.danger, fontSize: 14, fontWeight: '600' },
  meta: { color: colors.muted, fontSize: 14 },
});
