import { Platform } from 'react-native';

import { shareScoreCard } from './shareCard';

const mockSharing = { isAvailableAsync: jest.fn(), shareAsync: jest.fn() };
const mockFiles = { exists: false, deleted: 0, downloaded: [] as string[] };

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
    get exists() {
      return mockFiles.exists;
    }
    delete() {
      mockFiles.deleted += 1;
    }
    static downloadFileAsync = async (url: string, destination: File) => {
      mockFiles.downloaded.push(url);
      return destination;
    };
  }
  return { __esModule: true, File, Paths: { cache: { uri: 'file:///cache' } } };
});

const card = {
  image_url: 'https://api.example.test/v1/score-cards/u/a.png?exp=1&sig=s',
  share_text: "I'm in the top 3% of Geese fans on Fandom Score.",
  top_label: 'Top 3%',
  is_estimated: false,
  expires_at: '2026-10-27T13:00:00Z',
};
const api = { GET: jest.fn() };

beforeEach(() => {
  jest.clearAllMocks();
  mockFiles.exists = false;
  mockFiles.deleted = 0;
  mockFiles.downloaded = [];
  api.GET.mockResolvedValue({ data: card, response: { ok: true, status: 200 } });
});

test('on a phone the card is downloaded and handed to the share sheet', async () => {
  Platform.OS = 'ios';
  mockSharing.isAvailableAsync.mockResolvedValue(true);
  mockFiles.exists = true;   // an older copy is replaced

  expect(await shareScoreCard(api as never, 'a1')).toBe('shared');
  expect(api.GET).toHaveBeenCalledWith('/me/scores/{artistId}/card', { params: { path: { artistId: 'a1' } } });
  expect(mockFiles.deleted).toBe(1);
  expect(mockFiles.downloaded).toEqual([card.image_url]);
  expect(mockSharing.shareAsync).toHaveBeenCalledWith('file:///cache/fandom-score-a1.png', {
    mimeType: 'image/png',
    UTI: 'public.png',
    dialogTitle: card.share_text,
  });
});

test('a phone without sharing says so', async () => {
  Platform.OS = 'android';
  mockSharing.isAvailableAsync.mockResolvedValue(false);
  expect(await shareScoreCard(api as never, 'a1')).toBe('unavailable');
  expect(mockSharing.shareAsync).not.toHaveBeenCalled();
});

describe('on the web', () => {
  const blob = new Blob(['png'], { type: 'image/png' });
  const nav = navigator as unknown as { canShare?: unknown; share?: unknown };

  beforeEach(() => {
    Platform.OS = 'web';
    global.fetch = jest.fn().mockResolvedValue({ ok: true, blob: () => Promise.resolve(blob) }) as never;
  });

  afterEach(() => {
    delete nav.canShare;
    delete nav.share;
  });

  test('the browser share sheet gets the image and the sentence; a cancel is not an error', async () => {
    nav.canShare = jest.fn(() => true);
    nav.share = jest.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(Object.assign(new Error('x'), { name: 'AbortError' }));
    expect(await shareScoreCard(api as never, 'a1')).toBe('shared');
    expect((nav.share as jest.Mock).mock.calls[0][0].text).toBe(card.share_text);
    expect(await shareScoreCard(api as never, 'a1')).toBe('cancelled');
  });

  test('without file sharing the image is downloaded', async () => {
    URL.createObjectURL = jest.fn(() => 'blob:card');
    URL.revokeObjectURL = jest.fn();
    const click = jest.fn();
    const created: { href?: string; download?: string; click: jest.Mock } = { click };
    (globalThis as { document?: unknown }).document = { createElement: jest.fn(() => created) };
    jest.useFakeTimers();   // the object URL is revoked after a delay
    try {
      expect(await shareScoreCard(api as never, 'a1')).toBe('downloaded');
      expect(click).toHaveBeenCalled();
      expect(created.download).toBe('fandom-score.png');
      expect(created.href).toBe('blob:card');
    } finally {
      jest.runOnlyPendingTimers();
      jest.useRealTimers();
      delete (globalThis as { document?: unknown }).document;
    }
  });
});
