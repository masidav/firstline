import { useEffect, useState } from 'react';
import { AccessibilityInfo, Platform } from 'react-native';

const QUERY = '(prefers-reduced-motion: reduce)';

/** The browser's reduced-motion media query, or null when there's no window (SSR, tests without jsdom). */
function webQuery(): MediaQueryList | null {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return null;
  return window.matchMedia(QUERY);
}

/**
 * Whether the fan asked their device for less motion: "Reduce Motion" on iOS, "Remove animations" on Android,
 * `prefers-reduced-motion: reduce` in the browser. False until the setting is known, and kept current while the
 * screen is mounted. Anything that moves on its own (screen transitions, a creeping progress bar, and the feed's
 * bounce-in and music-sheet scroll once they exist) checks this and shows its final state instead.
 */
export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);

  useEffect(() => {
    if (Platform.OS === 'web') {
      const query = webQuery();
      if (!query) return;
      setReduced(query.matches);
      const onChange = (event: MediaQueryListEvent) => setReduced(event.matches);
      query.addEventListener('change', onChange);
      return () => query.removeEventListener('change', onChange);
    }

    let mounted = true;
    void AccessibilityInfo.isReduceMotionEnabled().then((enabled) => {
      if (mounted) setReduced(enabled);
    });
    const subscription = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduced);
    return () => {
      mounted = false;
      subscription.remove();
    };
  }, []);

  return reduced;
}
