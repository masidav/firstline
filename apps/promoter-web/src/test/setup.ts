import '@testing-library/jest-dom/vitest';
import { expect } from 'vitest';
import type { AxeMatchers } from 'vitest-axe/matchers';
import * as axeMatchers from 'vitest-axe/matchers';

// vitest-axe's own extend-expect targets an older Vitest, so the matcher is registered and typed here.
expect.extend(axeMatchers);

declare module 'vitest' {
  interface Matchers<R extends void | Promise<void> = void | Promise<void>, T = unknown> extends AxeMatchers {}
}
