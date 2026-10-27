import { useCallback, useEffect, useState } from 'react';

import { errorMessage } from '../api/client';

/** Loads data with `load` (again whenever `deps` change or `reload` is called); keeps the last data while reloading. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string>();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps);

  const reload = useCallback(async () => {
    try {
      setData(await run());
      setError(undefined);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [run]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return { data, error, reload, setData };
}
