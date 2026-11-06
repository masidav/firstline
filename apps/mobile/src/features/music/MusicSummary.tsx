import { useEffect, useState } from 'react';

import { unwrap } from '../../api/client';
import { useSession } from '../../auth/SessionProvider';
import { Body } from '../../ui/components';
import { MUSIC_PROVIDERS, type MusicStatus, PROVIDER_NAMES } from './musicConnect';

/** "Apple Music: connected · Tidal: not connected" for the profile page; shows nothing until it knows. */
export function MusicSummary() {
  const { session } = useSession();
  const [items, setItems] = useState<MusicStatus[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = unwrap(await session.api.GET('/me/music')).items;
        if (!cancelled) setItems(result);
      } catch {
        // The profile page doesn't need it; the music screen explains any problem.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [session]);

  if (!items) return null;
  return <Body>{summarize(items)}</Body>;
}

export function summarize(items: MusicStatus[]): string {
  return MUSIC_PROVIDERS.map((provider) => {
    const connected = items.find((item) => item.provider === provider)?.connected ?? false;
    return `${PROVIDER_NAMES[provider]}: ${connected ? 'connected' : 'not connected'}`;
  }).join(' · ');
}
