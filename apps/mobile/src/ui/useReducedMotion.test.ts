import { act, renderHook, waitFor } from '@testing-library/react-native';
import { AccessibilityInfo, Platform } from 'react-native';

import { useReducedMotion } from './useReducedMotion';

beforeEach(() => jest.clearAllMocks());

afterEach(() => {
  jest.restoreAllMocks();
  delete (window as { matchMedia?: unknown }).matchMedia;
});

test('on a phone it reads the system setting, and follows later changes', async () => {
  jest.replaceProperty(Platform, 'OS', 'ios');
  jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(true);
  const remove = jest.fn();
  let listener: ((enabled: boolean) => void) | undefined;
  jest.spyOn(AccessibilityInfo, 'addEventListener').mockImplementation(((event: string, handler: (enabled: boolean) => void) => {
    expect(event).toBe('reduceMotionChanged');
    listener = handler;
    return { remove };
  }) as unknown as typeof AccessibilityInfo.addEventListener);

  const { result, unmount } = renderHook(() => useReducedMotion());
  expect(result.current).toBe(false); // unknown yet: no motion is held back until the setting is read
  await waitFor(() => expect(result.current).toBe(true));

  act(() => listener?.(false));
  expect(result.current).toBe(false);

  unmount();
  expect(remove).toHaveBeenCalled();
});

test('on the web it reads prefers-reduced-motion and follows the media query', () => {
  jest.replaceProperty(Platform, 'OS', 'web');
  const isReduceMotionEnabled = jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled');
  const listeners: ((event: { matches: boolean }) => void)[] = [];
  const query = {
    matches: true,
    addEventListener: jest.fn((_: string, handler: (event: { matches: boolean }) => void) => listeners.push(handler)),
    removeEventListener: jest.fn(),
  };
  (window as { matchMedia?: unknown }).matchMedia = jest.fn((q: string) => {
    expect(q).toBe('(prefers-reduced-motion: reduce)');
    return query;
  });

  const { result, unmount } = renderHook(() => useReducedMotion());
  expect(result.current).toBe(true);
  expect(isReduceMotionEnabled).not.toHaveBeenCalled();

  act(() => listeners.forEach((handler) => handler({ matches: false })));
  expect(result.current).toBe(false);

  unmount();
  expect(query.removeEventListener).toHaveBeenCalledWith('change', listeners[0]);
});

test('on the web without matchMedia (an old browser, server rendering) it stays false', () => {
  jest.replaceProperty(Platform, 'OS', 'web');
  const { result } = renderHook(() => useReducedMotion());
  expect(result.current).toBe(false);
});
