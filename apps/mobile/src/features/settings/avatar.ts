import { manipulateAsync, SaveFormat } from 'expo-image-manipulator';
import * as ImagePicker from 'expo-image-picker';

import { type Api, type Me, unwrap } from '../../api/client';
import { uploadPhoto } from '../submissions/photoUpload';

/** Wider photos are scaled down before upload; the API crops the picture square at 512 px anyway. */
export const AVATAR_WIDTH = 1024;

/** The picked photo as a JPEG at most AVATAR_WIDTH wide. Re-encoding bakes in the rotation and drops EXIF. */
export async function prepareAvatar(uri: string, width?: number): Promise<string> {
  const actions = width && width > AVATAR_WIDTH ? [{ resize: { width: AVATAR_WIDTH } }] : [];
  return (await manipulateAsync(uri, actions, { compress: 0.9, format: SaveFormat.JPEG })).uri;
}

/**
 * Lets the fan pick a photo (with the system's square crop where there is one), uploads it as a profile picture
 * (`POST /uploads` with purpose `avatar`), and makes it theirs (`PATCH /me`). Null when they backed out of the picker.
 */
export async function changePicture(api: Api, fetchImpl: typeof fetch = fetch): Promise<Me | null> {
  const result = await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'], quality: 1, allowsEditing: true, aspect: [1, 1] });
  const asset = result.canceled ? undefined : result.assets[0];
  if (!asset) return null;
  const prepared = await prepareAvatar(asset.uri, asset.width);
  const uploadId = await uploadPhoto(api, prepared, fetchImpl, 'avatar');
  return unwrap(await api.PATCH('/me', { body: { avatar_upload_id: uploadId } }));
}

/** Removes the fan's picture; its link stops working at once. */
export async function removePicture(api: Api): Promise<Me> {
  return unwrap(await api.PATCH('/me', { body: { avatar_upload_id: null } }));
}
