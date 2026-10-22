/**
 * @jest-environment node
 */
import { postPhoto, statusLabel, type Submission } from './photoUpload';

jest.mock('expo-image-manipulator', () => ({ manipulateAsync: jest.fn(), SaveFormat: { JPEG: 'jpeg' } }));

const ok = (data: unknown, status = 200) => ({ data, response: { ok: true, status } });

test('uploads to the signed link without a sign-in header, then submits the upload', async () => {
  const blob = new Blob([new Uint8Array(1234)], { type: 'image/jpeg' });
  const fetchImpl = jest
    .fn()
    .mockResolvedValueOnce({ blob: async () => blob })
    .mockResolvedValueOnce({ ok: true, status: 204 });
  const api = {
    POST: jest
      .fn()
      .mockResolvedValueOnce(
        ok({ upload_id: 'u1', method: 'PUT', url: 'https://api.test/v1/uploads/u1/content?exp=1&sig=s', headers: { 'Content-Type': 'image/jpeg' }, expires_at: 'x' }, 201),
      )
      .mockResolvedValueOnce(ok({ id: 's1', status: 'needs_review' }, 201)),
  };

  const submission = await postPhoto(
    api as never,
    { artistId: 'a1', kind: 'concert_photo', caption: '  Front row  ', photoUri: 'file:///photo.jpg' },
    fetchImpl as never,
  );

  expect(submission).toEqual({ id: 's1', status: 'needs_review' });
  expect(api.POST).toHaveBeenNthCalledWith(1, '/uploads', { body: { purpose: 'submission', mime_type: 'image/jpeg', bytes: 1234 } });
  expect(fetchImpl).toHaveBeenNthCalledWith(2, 'https://api.test/v1/uploads/u1/content?exp=1&sig=s', {
    method: 'PUT',
    headers: { 'Content-Type': 'image/jpeg' },
    body: blob,
  });
  expect(api.POST).toHaveBeenNthCalledWith(2, '/submissions', {
    body: { artist_id: 'a1', kind: 'concert_photo', upload_ids: ['u1'], caption: 'Front row' },
  });
});

test('an unreadable photo is explained and nothing is submitted', async () => {
  const fetchImpl = jest
    .fn()
    .mockResolvedValueOnce({ blob: async () => new Blob(['x']) })
    .mockResolvedValueOnce({ ok: false, status: 422 });
  const api = { POST: jest.fn().mockResolvedValueOnce(ok({ upload_id: 'u1', method: 'PUT', url: 'https://x', expires_at: 'x' }, 201)) };

  await expect(
    postPhoto(api as never, { artistId: 'a1', kind: 'merch', caption: '', photoUri: 'file:///p.jpg' }, fetchImpl as never),
  ).rejects.toThrow("That photo couldn't be read");
  expect(api.POST).toHaveBeenCalledTimes(1);
});

test('status labels', () => {
  const base = { status: 'needs_review', shared_at: null, rejection_reason: null } as unknown as Submission;
  expect(statusLabel(base)).toBe('Waiting for review');
  expect(statusLabel({ ...base, status: 'verified', shared_at: '2026-10-21T00:00:00Z' })).toBe('Verified · on the feed');
  expect(statusLabel({ ...base, status: 'rejected', rejection_reason: 'Blurry' })).toBe('Not accepted: Blurry');
});
