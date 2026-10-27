# TODO — Promoter web dashboard

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Choose the web stack (proposed: React + TypeScript, sharing the client generated from `openapi.yaml`).
      *2026-10-27: Vite + React 19 + TypeScript, react-router, openapi-fetch with types generated from `openapi.yaml`.*
- [x] Organization sign-in and member management. *Google and developer sign-in, a handle for new accounts; owners
      add, re-role, and remove members.*
- [ ] Create and edit events and allocation pools; preview eligible-fan counts before opening registration.
      *Shows and allocations done 2026-10-27 (times in the show city's time zone); the eligible-fan preview needs an API.*
- [x] Monitor registration and selection in near real time; trigger or schedule the draw. *Registrations refresh
      every 10 s while open; the draw is queued and followed every 3 s until done.*
- [ ] Export reports and consented fan data; download the case-study metrics. *CSV of registrations and tickets done;
      consented fan data and case-study metrics not yet.*
- [ ] Accessibility (WCAG 2.2 AA) and responsive layout. *Labelled controls and alerts; no audit or axe checks yet.*
- [x] Component tests (Vitest) and end-to-end tests (Playwright). *10 component tests, 3 journeys, both in CI.*

## Verification
- [x] Add or update automated tests for the behavior above.
- [x] Run the relevant tests and record actual results. *2026-10-27: Vitest 10 passed, Playwright journeys 3 passed
      against the real API; the journeys found a hook-order crash after sign-in and a "Drawn" label on selected fans,
      both fixed.*
- [x] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
