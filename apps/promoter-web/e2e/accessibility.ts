import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

/** What a scan must not find: WCAG 2.0–2.2 A and AA, plus axe's best practices (landmarks, heading order, ARIA use). */
export const AXE_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'];

/** Violations at these impacts fail the check; minor and moderate ones are reported in the test output only. */
const FAILING_IMPACTS = new Set(['serious', 'critical']);

/**
 * Runs axe on the page as it is now and fails on serious or critical violations. Call it once the page has finished
 * loading (assert on something visible first): axe sees the DOM, not what is still being fetched.
 */
export async function expectAccessible(page: Page, name: string) {
  const results = await new AxeBuilder({ page }).withTags(AXE_TAGS).analyze();
  const describe = (v: (typeof results.violations)[number]) =>
    `${v.impact}: ${v.id} (${v.help})\n` + v.nodes.map((n) => `    ${n.target.join(' ')}\n      ${n.failureSummary?.replace(/\n/g, '\n      ')}`).join('\n');
  const failing = results.violations.filter((v) => FAILING_IMPACTS.has(v.impact ?? ''));
  const lesser = results.violations.filter((v) => !FAILING_IMPACTS.has(v.impact ?? ''));
  if (lesser.length > 0) console.log(`axe (${name}), not failing:\n  ${lesser.map(describe).join('\n  ')}`);
  expect(failing.map(describe), `axe (${name}) found ${failing.length} serious or critical violation(s)`).toEqual([]);
}
