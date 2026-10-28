import { Platform } from 'react-native';

import { ApiError } from '../../api/client';
import { downloadMyData, exportFileName, fileNameFrom } from './downloadMyData';

const mockSharing = { isAvailableAsync: jest.fn(), shareAsync: jest.fn() };
const mockFiles = { created: [] as { uri: string; options: unknown }[], written: [] as { uri: string; text: string }[] };

jest.mock('expo-sharing', () => ({
  __esModule: true,
  isAvailableAsync: (...a: unknown[]) => mockSharing.isAvailableAsync(...a),
  shareAsync: (...a: unknown[]) => mockSharing.shareAsync(...a),
}));
jest.mock('expo-file-system', () => {
  class File {
    uri: string;
    constructor(dir: { uri: string }, name: string) {
      this.uri = `${dir.uri}/${name}`;
    }
    create(options: unknown) {
      mockFiles.created.push({ uri: this.uri, options });
    }
    write(text: string) {
      mockFiles.written.push({ uri: this.uri, text });
    }
  }
  return { __esModule: true, File, Paths: { cache: { uri: 'file:///cache' } } };
});

const body = '{\n  "format_version": 1\n}';
const exported = (disposition: string | null) => ({
  data: body,
  response: { ok: true, status: 200, headers: new Headers(disposition ? { 'content-disposition': disposition } : {}) },
});
const api = { GET: jest.fn() };

beforeEach(() => {
  jest.clearAllMocks();
  mockFiles.created = [];
  mockFiles.written = [];
  api.GET.mockResolvedValue(exported('attachment; filename=fandom-score-export-2026-10-27.json; filename*=UTF-8\'\'fandom-score-export-2026-10-27.json'));
});

test('the file name comes from the API, or is built the same way', () => {
  expect(fileNameFrom('attachment; filename="fandom-score-export-2026-10-27.json"')).toBe('fandom-score-export-2026-10-27.json');
  expect(fileNameFrom('attachment; filename=../../evil.sh')).toBeNull();
  expect(fileNameFrom(null)).toBeNull();
  expect(exportFileName(new Date('2026-10-27T23:59:00Z'))).toBe('fandom-score-export-2026-10-27.json');
});

test('on a phone the export is written to the cache and handed to the share sheet', async () => {
  Platform.OS = 'ios';
  mockSharing.isAvailableAsync.mockResolvedValue(true);

  expect(await downloadMyData(api as never)).toBe('shared');
  expect(api.GET).toHaveBeenCalledWith('/me/export', { parseAs: 'text' });
  const uri = 'file:///cache/fandom-score-export-2026-10-27.json';
  expect(mockFiles.created).toEqual([{ uri, options: { overwrite: true } }]);
  expect(mockFiles.written).toEqual([{ uri, text: body }]);
  expect(mockSharing.shareAsync).toHaveBeenCalledWith(uri, {
    mimeType: 'application/json',
    UTI: 'public.json',
    dialogTitle: 'Your Fandom Score data',
  });
});

test('a phone without sharing says so and writes nothing', async () => {
  Platform.OS = 'android';
  mockSharing.isAvailableAsync.mockResolvedValue(false);
  expect(await downloadMyData(api as never)).toBe('unavailable');
  expect(mockFiles.written).toEqual([]);
  expect(mockSharing.shareAsync).not.toHaveBeenCalled();
});

test('an API error is thrown as ApiError (e.g. the hourly limit)', async () => {
  Platform.OS = 'ios';
  api.GET.mockResolvedValue({ error: { code: 'rate_limited', title: 'Too Many Requests' }, response: { ok: false, status: 429, headers: new Headers() } });
  await expect(downloadMyData(api as never)).rejects.toBeInstanceOf(ApiError);
  expect(mockFiles.written).toEqual([]);
});

test('on the web the export is saved as a download', async () => {
  Platform.OS = 'web';
  api.GET.mockResolvedValue(exported(null));   // CORS may hide the header: the name is built instead
  URL.createObjectURL = jest.fn(() => 'blob:export');
  URL.revokeObjectURL = jest.fn();
  const click = jest.fn();
  const created: { href?: string; download?: string; click: jest.Mock } = { click };
  (globalThis as { document?: unknown }).document = { createElement: jest.fn(() => created) };
  jest.useFakeTimers();   // the object URL is revoked after a delay
  try {
    expect(await downloadMyData(api as never)).toBe('downloaded');
    expect(click).toHaveBeenCalled();
    expect(created.download).toBe(exportFileName());
    expect(created.href).toBe('blob:export');
    const blob = (URL.createObjectURL as jest.Mock).mock.calls[0][0] as Blob;
    expect(blob.type).toBe('application/json');
  } finally {
    jest.runOnlyPendingTimers();
    jest.useRealTimers();
    delete (globalThis as { document?: unknown }).document;
  }
});
