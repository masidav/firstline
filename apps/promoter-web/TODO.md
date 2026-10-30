# TODO — Promoter web dashboard

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Choose the web stack (proposed: React + TypeScript, sharing the client generated from `openapi.yaml`).
      *2026-10-27: Vite + React 19 + TypeScript, react-router, openapi-fetch with types generated from `openapi.yaml`.*
- [x] Organization sign-in and member management. *Google and developer sign-in, a handle for new accounts; owners
      add, re-role, and remove members.*
- [x] Create and edit events and allocation pools; preview eligible-fan counts before opening registration.
      *Shows and allocations done 2026-10-27 (times in the show city's time zone). Eligible-fan preview done 2026-10-30:
      the allocation page's "Eligible fans" card (`GET .../eligibility-preview`) shows fans with a score, how many clear
      the threshold (local apart from elsewhere) against the tickets, eligible fans per ticket, and a table of the same
      counts at other thresholds; it reloads after the rules are edited and stays as information once registration has
      opened. Counts only, never a fan. Verified: Vitest 24 (one new test), `npm run typecheck`. No Playwright journey
      yet (the real API needs seeded fans with scores).*
- [x] Monitor registration and selection in near real time; trigger or schedule the draw. *Registrations refresh
      every 10 s while open; the draw is queued and followed every 3 s until done.*
- [ ] Export reports and consented fan data; download the case-study metrics. *CSV of registrations and tickets done.
      Case-study metrics done 2026-10-30: the show page's "Case-study report" (`ShowReport.tsx`) shows every published
      allocation beside the roll-up with a CSV download, and admins record scalping complaints per allocation.
      Verified: Vitest 25 (2 new), typecheck. Consented fan data not yet.*
- [ ] Accessibility (WCAG 2.2 AA) and responsive layout. *2026-10-30: axe scans in CI, every page the journeys visit
      (`e2e/accessibility.spec.ts`, serious/critical fail) plus vitest-axe on error states (`src/accessibility.test.tsx`).
      The first scan found a red status pill at 4.36:1 contrast (darkened to #b0262c), no `h1` on organization pages
      (the organization's name is now the `h1`), buttons posing as list items in the search picker (now a real list),
      and empty action-column headers (now hidden "Actions" text); all fixed, scans clean at every impact. No manual
      screen-reader or keyboard audit yet; responsive layout not checked below 1280 px.*
- [x] Component tests (Vitest) and end-to-end tests (Playwright). *27 component tests, 3 journeys plus the
      accessibility scan, all in CI.*
- [ ] Host it on beta. *2026-10-28: `Dockerfile` target `promoter-web` (nginx, settings from `/config.js` at
      startup), Kubernetes manifests, its own hostname and certificate (`promoters.beta.firstline.com`). The image
      passed the Playwright journeys locally; not live until DNS, the certificate, and deploys are on.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. *2026-10-27: Vitest 10 passed, Playwright journeys 3 passed
      against the real API; the journeys found a hook-order crash after sign-in and a "Drawn" label on selected fans,
      both fixed. 2026-10-30: `npm run typecheck` clean, Vitest 26 passed, `e2e/run.sh` 4 passed (3 journeys and the
      axe scan) in Chrome.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
