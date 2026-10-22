import type { components } from '../../api/client';

export type AlbumList = components['schemas']['AlbumList'];
export type AlbumListDetail = components['schemas']['AlbumListDetail'];
export type ListItem = NonNullable<AlbumListDetail['items']>[number];

export function describeList(list: AlbumList): string {
  const count = list.item_count === 1 ? '1 album' : `${list.item_count} albums`;
  return list.visibility === 'public' ? count : `${count} · ${list.visibility}`;
}
