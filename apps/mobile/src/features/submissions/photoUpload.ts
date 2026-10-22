import { manipulateAsync, SaveFormat } from 'expo-image-manipulator';

import { type Api, type components, unwrap } from '../../api/client';

export type Submission = components['schemas']['Submission'];
export type SubmissionKind = components['schemas']['SubmissionKind'];

/** The kinds a fan can post, in the order the app offers them. Screenshots are proof-only and not offered yet. */
export const POST_KINDS: { kind: SubmissionKind; label: string; hint: string }[] = [
  { kind: 'concert_photo', label: 'Concert photo', hint: 'A photo you took at the show.' },
  { kind: 'merch', label: 'Merch', hint: 'Merch you bought: a shirt, a record, a poster.' },
  { kind: 'ticket', label: 'Ticket', hint: 'Proof only: tickets are never shown to other fans.' },
];

export const MAX_WIDTH = 2048;

/**
 * Re-encodes the picked photo as a JPEG at most MAX_WIDTH wide. Re-encoding bakes in the rotation and drops EXIF
 * (location, camera details); the server strips metadata again regardless.
 */
export async function preparePhoto(uri: string, width?: number): Promise<string> {
  const actions = width && width > MAX_WIDTH ? [{ resize: { width: MAX_WIDTH } }] : [];
  const result = await manipulateAsync(uri, actions, { compress: 0.85, format: SaveFormat.JPEG });
  return result.uri;
}

/** Uploads one prepared JPEG: asks the API for an upload slot, then PUTs the bytes to its signed link. */
export async function uploadPhoto(api: Api, uri: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const blob = await (await fetchImpl(uri)).blob();
  const intent = unwrap(
    await api.POST('/uploads', { body: { purpose: 'submission', mime_type: 'image/jpeg', bytes: blob.size } }),
  );
  // The link is signed, so no sign-in header (like a storage provider's presigned URL).
  const put = await fetchImpl(intent.url, { method: intent.method, headers: intent.headers ?? {}, body: blob });
  if (!put.ok) {
    throw new Error(put.status === 422 ? "That photo couldn't be read. Try another one." : `Upload failed (${put.status}).`);
  }
  return intent.upload_id;
}

/** Uploads the photo and submits it as proof for the artist. */
export async function postPhoto(
  api: Api,
  input: { artistId: string; kind: SubmissionKind; caption: string; photoUri: string },
  fetchImpl: typeof fetch = fetch,
): Promise<Submission> {
  const uploadId = await uploadPhoto(api, input.photoUri, fetchImpl);
  return unwrap(
    await api.POST('/submissions', {
      body: {
        artist_id: input.artistId,
        kind: input.kind,
        upload_ids: [uploadId],
        caption: input.caption.trim() || null,
      },
    }),
  );
}

export function kindLabel(kind: SubmissionKind): string {
  return POST_KINDS.find((k) => k.kind === kind)?.label ?? 'Screenshot';
}

export function statusLabel(submission: Submission): string {
  switch (submission.status) {
    case 'verified':
      return submission.shared_at ? 'Verified · on the feed' : 'Verified';
    case 'rejected':
      return submission.rejection_reason ? `Not accepted: ${submission.rejection_reason}` : 'Not accepted';
    default:
      return 'Waiting for review';
  }
}
