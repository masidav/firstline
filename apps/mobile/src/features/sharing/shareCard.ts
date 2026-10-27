import { File as DeviceFile, Paths } from 'expo-file-system';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';

import { type Api, unwrap } from '../../api/client';

/**
 * Sharing a score card (GET /me/scores/{artistId}/card): a signed link to a PNG and a sentence. On the phone the image
 * is downloaded and handed to the share sheet (Instagram, X, Messages, ...). On the web it goes to the browser's share
 * sheet when it can take files, and is downloaded otherwise.
 */
export type ShareOutcome = 'shared' | 'downloaded' | 'cancelled' | 'unavailable';

export async function shareScoreCard(api: Api, artistId: string): Promise<ShareOutcome> {
  const card = unwrap(await api.GET('/me/scores/{artistId}/card', { params: { path: { artistId } } }));
  return Platform.OS === 'web' ? shareOnWeb(card.image_url, card.share_text) : shareOnPhone(card.image_url, card.share_text, artistId);
}

async function shareOnPhone(imageUrl: string, text: string, artistId: string): Promise<ShareOutcome> {
  if (!(await Sharing.isAvailableAsync())) return 'unavailable';
  const destination = new DeviceFile(Paths.cache, `fandom-score-${artistId}.png`);
  if (destination.exists) destination.delete();
  const image = await DeviceFile.downloadFileAsync(imageUrl, destination);
  // The share sheet resolves when it closes, shared or not; the platforms don't say which.
  await Sharing.shareAsync(image.uri, { mimeType: 'image/png', UTI: 'public.png', dialogTitle: text });
  return 'shared';
}

async function shareOnWeb(imageUrl: string, text: string): Promise<ShareOutcome> {
  const response = await fetch(imageUrl);
  if (!response.ok) throw new Error(`The score card couldn't be loaded (${response.status}).`);
  const blob = await response.blob();
  const file = new globalThis.File([blob], 'fandom-score.png', { type: 'image/png' });
  if (typeof navigator !== 'undefined' && navigator.canShare?.({ files: [file] })) {
    try {
      await navigator.share({ files: [file], text });
      return 'shared';
    } catch (e) {
      if (e instanceof Error && e.name === 'AbortError') return 'cancelled';
      throw e;
    }
  }
  // No file sharing here (most desktop browsers): save the image instead.
  const href = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = href;
  link.download = 'fandom-score.png';
  link.click();
  setTimeout(() => URL.revokeObjectURL(href), 10_000);
  return 'downloaded';
}
