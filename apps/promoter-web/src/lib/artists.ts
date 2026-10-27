import { useEffect, useState } from 'react';

import type { Api } from '../api/client';

/** Artist names for a list of shows (GET /artists/{id}); an artist Fandom Score no longer lists shows as "Artist". */
export function useArtistNames(api: Api, ids: string[]): Record<string, string> {
  const [names, setNames] = useState<Record<string, string>>({});
  const key = [...new Set(ids)].sort().join(',');
  useEffect(() => {
    let cancelled = false;
    void Promise.all(
      key.split(',').filter(Boolean).map(async (artistId) => {
        const result = await api.GET('/artists/{artistId}', { params: { path: { artistId } } });
        return [artistId, result.data?.name ?? 'Artist'] as const;
      }),
    ).then((pairs) => {
      if (!cancelled) setNames(Object.fromEntries(pairs));
    });
    return () => {
      cancelled = true;
    };
  }, [api, key]);
  return names;
}
