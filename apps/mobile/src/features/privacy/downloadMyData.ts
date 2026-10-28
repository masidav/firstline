import { File as DeviceFile, Paths } from 'expo-file-system';
import * as Sharing from 'expo-sharing';
import { Platform } from 'react-native';

import { type Api, unwrap } from '../../api/client';

/**
 * "Download my data" (GET /me/export): one JSON file with everything the app holds about the fan. The file is fetched
 * with the session's API client (it needs the sign-in), then saved: on the web as a browser download, on a phone
 * written to the cache and handed to the share sheet ("Save to Files", Drive, mail, ...).
 */
export type DownloadOutcome = 'downloaded' | 'shared' | 'unavailable';

/** The name the API gives the file (Content-Disposition), for when the header can't be read. */
export function exportFileName(now: Date = new Date()): string {
  return `fandom-score-export-${now.toISOString().slice(0, 10)}.json`;
}

/** The filename in a Content-Disposition header, or null. Only names the API would give are accepted. */
export function fileNameFrom(contentDisposition: string | null): string | null {
  const match = contentDisposition?.match(/filename="?(fandom-score-export-\d{4}-\d{2}-\d{2}\.json)"?/);
  return match?.[1] ?? null;
}

export async function downloadMyData(api: Api): Promise<DownloadOutcome> {
  const result = await api.GET('/me/export', { parseAs: 'text' });
  const text = unwrap(result) as unknown as string;
  const name = fileNameFrom(result.response.headers.get('content-disposition')) ?? exportFileName();
  return Platform.OS === 'web' ? saveOnWeb(text, name) : shareOnPhone(text, name);
}

async function shareOnPhone(text: string, name: string): Promise<DownloadOutcome> {
  if (!(await Sharing.isAvailableAsync())) return 'unavailable';
  const file = new DeviceFile(Paths.cache, name);
  file.create({ overwrite: true });
  file.write(text);
  // The share sheet resolves when it closes, saved or not; the platforms don't say which.
  await Sharing.shareAsync(file.uri, { mimeType: 'application/json', UTI: 'public.json', dialogTitle: 'Your Fandom Score data' });
  return 'shared';
}

function saveOnWeb(text: string, name: string): DownloadOutcome {
  const href = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = href;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(href), 10_000);
  return 'downloaded';
}
